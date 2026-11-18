// PWL-3, first half — a wall port resolves to a lumped gap from a PEC face of the air box to the line, the face is forced PEC
// with no padding, and the line is cut back by the gap, in the solved problem only. `docs/briefs/brief-pwl-3-…`.
// No solver runs here: the problem is the run's own assembly.

using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Tests.Em3d;
using RfCore;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class WallPortTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pwl3-" + Guid.NewGuid().ToString("N")[..12]);

    public WallPortTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long W = 1717866, B = 2000000, Len = 20000000;   // DBU (nm): the testdata/pwl stripline

    private static JsonObject WallPort(int n, long offset, long yLo = -W, long ySize = 2 * W) => new()
    {
        ["Number"] = n, ["Name"] = $"P{n}", ["Kind"] = "Wall", ["Plane"] = "YZ", ["Offset"] = offset,
        ["Rect"] = new JsonObject { ["Min"] = new JsonArray(yLo, -B / 2), ["Size"] = new JsonArray(ySize, B) },
        ["Z0"] = "50",
    };

    /// <summary>testdata/pwl's 20 mm stripline with its strip run to both faces and a Kind Wall port drawn on each, the setup's
    /// x faces stated absorbing and padded (the run makes them PEC and flush), edited by <paramref name="edit"/>.</summary>
    private (C3dDocument Doc, string Path) Stripline(Action<JsonObject>? edit = null)
    {
        string dir = FixturePaths.Require("testdata/pwl");
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "stripline_line_L20000um.c3d")))!.AsObject();
        foreach (var o in doc["Objects"]!.AsArray())
            if ((string?)o!["Name"] == "strip") { o["Rect"]!["Min"]![0] = 0; o["Rect"]!["Size"]![0] = Len; }
        doc["Ports"] = new JsonArray(WallPort(1, 0), WallPort(2, Len));
        var box = doc["Setups"]![0]!["AirBox"]!.AsObject();
        box["XMin"] = new JsonObject { ["PaddingUm"] = 500, ["Boundary"] = "Absorbing" };
        edit?.Invoke(doc);
        File.Copy(Path.Combine(dir, "pwl.ctech"), Path.Combine(_root, "pwl.ctech"), overwrite: true);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6] + ".c3d");
        File.WriteAllText(path, doc.ToJsonString());
        return (C3dPersistence.LoadFromFile(path), path);
    }

    private static Em3dGenerationResult Assemble(C3dDocument doc, string path, string setup = "Palace")
    {
        var embedded = C3dSetups.Read(doc).Single(s => s.Name == setup).Setup!;
        return C3dProblemAssembly.Assemble(C3dSetups.ForRun(embedded, path), doc, path, null);
    }

    [Fact]
    public void AWallPortIsAGapFromThePecFaceToTheLine_AndTheLineIsCutBackByIt()
    {
        var (doc, path) = Stripline();
        var built = Assemble(doc, path);
        Assert.True(built.Ok, built.Refusal);
        var problem = built.Problem!;

        // The x faces are PEC and flush with the structure, whatever the setup stated.
        Assert.Equal(Em3dBoundaryKind.Pec, problem.Boundary.Faces.XMin);
        Assert.Equal(Em3dBoundaryKind.Pec, problem.Boundary.Faces.XMax);
        Assert.Equal(0, problem.Boundary.Min.X, 12);
        Assert.Equal(20e-3, problem.Boundary.Max.X, 12);

        // g = min(w, h)/5 = min(1717.866, 1000)/5 µm = 200 µm; a lumped port from the wall (−) to the line (+).
        Assert.Equal(2, problem.Ports.Count);
        var p1 = problem.Ports.Single(p => p.Number == 1);
        Assert.Equal(Em3dPortKind.Lumped, p1.Kind);
        Assert.NotNull(p1.Wall);
        Assert.Equal(200e-6, p1.Wall!.GapM, 12);
        Assert.Equal(1e-3, p1.Wall.HeightM, 12);
        Assert.Equal(1717.866e-6, p1.Wall.WidthM, 12);
        Assert.Equal("airbox/xmin", p1.NegativeObject);
        Assert.Equal("strip", p1.PositiveObject);
        Assert.Equal(new Point3(0, -858.933e-6, 0), p1.Min);
        Assert.Equal(200e-6, p1.Max.X, 12);
        Assert.Equal(new Point3(1, 0, 0), p1.Direction);
        var p2 = problem.Ports.Single(p => p.Number == 2);
        Assert.Equal("airbox/xmax", p2.NegativeObject);
        Assert.Equal(new Point3(-1, 0, 0), p2.Direction);
        Assert.Equal(19800e-6, p2.Min.X, 12);

        // The strip, cut back 200 µm at each end in the solved problem.
        var strip = problem.Sheets.Single(s => s.Name == "strip");
        Assert.Equal(200e-6, strip.Outline.Min(q => q.X), 12);
        Assert.Equal(19800e-6, strip.Outline.Max(q => q.X), 12);

        // And the run says the face was forced and the gap is calibrated out.
        Assert.Contains(built.Notes, n => n.Contains("PEC with no padding", StringComparison.Ordinal));
        Assert.Contains(built.Notes, n => n.Contains("calibrated out by line standards", StringComparison.Ordinal));
    }

    /// <summary>R-pwl3-7 — the standards are the face's own cross-section, extruded wall to wall: the short one 6 heights long,
    /// the strip keeping its gap at both ends, both walls PEC, and the far port the near one mirrored.</summary>
    [Fact]
    public void TheStandardsAreTheFacesCrossSectionExtrudedBetweenTwoPecWalls()
    {
        var (doc, path) = Stripline();
        var problem = Assemble(doc, path).Problem!;
        var notes = new List<string>();
        var (cal, refusal) = CircuitRF.Design.Em3d.Em3dWallCalibration.Plan(problem, 1e9, 10e9, notes);
        Assert.Null(refusal);
        // Two faces, each one group of one line, with ONE cross-section: they share three standards (1–10 GHz is two separations).
        Assert.Equal(3, cal!.Standards.Count);
        Assert.Contains(notes, n => n.Contains("the xmin face's own", StringComparison.Ordinal));

        var shortStd = cal.Standards.First(s => s.Label.StartsWith("wall xmin", StringComparison.Ordinal));
        Assert.Equal(6e-3, shortStd.LengthM, 12);                         // 6 h, h = 1 mm
        var sp = shortStd.Problem;
        Assert.Equal(0, sp.Boundary.Min.X, 12);
        Assert.Equal(6e-3, sp.Boundary.Max.X, 12);
        Assert.Equal(Em3dBoundaryKind.Pec, sp.Boundary.Faces.XMin);
        Assert.Equal(Em3dBoundaryKind.Pec, sp.Boundary.Faces.XMax);
        Assert.Equal(problem.Boundary.Min.Y, sp.Boundary.Min.Y, 12);     // the face's own width and height
        Assert.Equal(problem.Boundary.Max.Z, sp.Boundary.Max.Z, 12);

        var strip = sp.Sheets.Single(x => x.Name == "strip");
        Assert.Equal(200e-6, strip.Outline.Min(q => q.X), 12);
        Assert.Equal(5800e-6, strip.Outline.Max(q => q.X), 12);
        Assert.Equal(-858.933e-6, strip.Outline.Min(q => q.Y), 12);
        var fill = sp.Solids.Single(x => x.Name == "fill");
        Assert.Equal(new Point3(0, problem.Boundary.Min.Y, -1e-3), ((Em3dBox)fill.Primitive).Min);

        Assert.Equal(2, sp.Ports.Count);
        var near = sp.Ports[0]; var far = sp.Ports[1];
        Assert.Equal("airbox/xmin", near.NegativeObject);
        Assert.Equal("airbox/xmax", far.NegativeObject);
        Assert.Equal(5800e-6, far.Min.X, 12);
        Assert.Equal(6e-3, far.Max.X, 12);
        Assert.Equal(new Point3(-1, 0, 0), far.Direction);
        Assert.Contains(notes, n => n.Contains("3 line standard(s) of 6", StringComparison.Ordinal));
    }

    /// <summary>
    /// Overview gate 3, as restated at PWL-2 — the stripline through `circuitrf em` on Palace (Standard quality, no refinement): its
    /// three shared standards solved, the structure calibrated, against the exact 50 Ω line of ε_eff 2.1. Measured at PWL-3: VSWR
    /// 1.0014–1.0046, ε_eff 2.117, |ΔS21| 2.4e-2 at 10 GHz (Palace's own phase velocity over 20 mm, not the port).
    /// </summary>
    [DcRowFact(Em3dSolverId.Palace)]
    [Trait("Category", "Benchmark")]
    public void Gate3_TheCalibratedStriplineIsTheExactLine()
    {
        var (_, path) = Stripline();
        string snp = Path.Combine(_root, "line.s2p");
        var (code, _, err) = RunCli("em", path, "--setup", "Palace", "-o", snp);
        Assert.True(code == 0, err);
        Assert.Contains("wall standard(s) solved", err);
        var s = TouchstoneIO.ReadFile(snp);
        const double C0 = 2.99792458e8;
        for (int k = 0; k < s.Frequencies.Length; k++)
        {
            double f = s.Frequencies[k], theta = 2 * Math.PI * f * Math.Sqrt(2.1) / C0 * 0.02;
            var m = s.Matrices[k];
            double vswr = Math.Max(RfHelpers.VswrFromGamma(Complex.Zero, m[0, 0]), RfHelpers.VswrFromGamma(Complex.Zero, m[1, 1]));
            Assert.True(vswr <= 1.01, $"VSWR {vswr:F4} at {f / 1e9} GHz");
            Assert.True((m[1, 0] - Complex.Exp(-Complex.ImaginaryOne * theta)).Magnitude <= 3e-2, $"|ΔS21| at {f / 1e9} GHz");
            double ph = -m[1, 0].Phase, beta = (ph + 2 * Math.PI * Math.Round((theta - ph) / (2 * Math.PI))) / 0.02;
            double eeff = Math.Pow(beta * C0 / (2 * Math.PI * f), 2);
            Assert.True(Math.Abs(eeff / 2.1 - 1) <= 0.01, $"ε_eff {eeff:F4} at {f / 1e9} GHz");
        }
    }

    /// <summary>
    /// Overview gate 4 on the product (restated by brief 2): testdata/pwl's edge-coupled microstrip pair with a Kind Wall port on each
    /// line at each end, run by the CLI on Palace at Standard (about 25 minutes, most of it the three 4-port standards). Against kernel
    /// A's quasi-static coupled line, computed here on the pair's own cross-section: every port's VSWR ≤ 1.025, each mode's ε_eff at
    /// 1 GHz within 3.5 % (the reference is quasi-static; the owner moved the brief's 3 %, see the check), and the standards
    /// agreeing to 1.5e-2. RESOLVED §PWL-3 measured VSWR 1.0174 / 1.0046 / 1.0062 at 1 / 5 / 10 GHz.
    /// </summary>
    [DcRowFact(Em3dSolverId.Palace)]
    [Trait("Category", "Benchmark")]
    public void Gate4_TheCalibratedPairIsKernelAsCoupledLine()
    {
        string dir = FixturePaths.Require("testdata/pwl");
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "coupled_line_L20000um.c3d")))!.AsObject();
        foreach (var o in doc["Objects"]!.AsArray())
            if (((string?)o!["Name"])!.StartsWith("strip", StringComparison.Ordinal)) { o["Rect"]!["Min"]![0] = 0; o["Rect"]!["Size"]![0] = Len; }
        JsonObject Port(int n, long at, long y) => new()
        {
            ["Number"] = n, ["Name"] = $"P{n}", ["Kind"] = "Wall", ["Plane"] = "YZ", ["Offset"] = at, ["Z0"] = "50",
            ["Rect"] = new JsonObject { ["Min"] = new JsonArray(y, 0), ["Size"] = new JsonArray(1200000, 1000000) },
        };
        doc["Ports"] = new JsonArray(Port(1, 0, -1350000), Port(2, 0, 150000), Port(3, Len, -1350000), Port(4, Len, 150000));
        File.Copy(Path.Combine(dir, "pwl.ctech"), Path.Combine(_root, "pwl.ctech"), overwrite: true);
        string path = Path.Combine(_root, "pair.c3d");
        File.WriteAllText(path, doc.ToJsonString());
        string snp = Path.Combine(_root, "pair.s4p");
        var (code, _, err) = RunCli("em", path, "--setup", "Palace", "-o", snp);
        Assert.True(code == 0, err);
        AssertGate4(TouchstoneIO.ReadFile(snp), err);
    }

    /// <summary>Gate 4's three checks on a published 4-port (ports: a near, b near, a far, b far) and the run's notes.</summary>
    internal static void AssertGate4(SNP s, string notes)
    {
        const double C0 = 2.99792458e8, H = 508e-6, Er = 2.2, W = 1200e-6, Gap = 300e-6, L = 0.02;
        // Kernel A on the pair's cross-section (1 µm strips: its mesh needs a thickness), [C] symmetrised, the modes of L·C.
        const double T = 1e-6;
        CircuitRF.Engine.Mom.EmConductor Strip(string n, double y0, double y1)
            => new(n, [new(y0, H), new(y1, H), new(y1, H + T), new(y0, H + T)], 5.8e7);
        var problem = new CircuitRF.Engine.Mom.EmProblem([Strip("a", -Gap / 2 - W, -Gap / 2), Strip("b", Gap / 2, Gap / 2 + W)],
            [new(0, H, new CircuitRF.Engine.Mom.EmMaterial(Er)), new(H, 1.0, CircuitRF.Engine.Mom.EmMaterial.Air)],
            new CircuitRF.Engine.Mom.EmGroundPlane(0, 5.8e7), [], 1e-3);
        var m = CircuitRF.Engine.Mom.RlgcExtractor.Extract(problem,
            CircuitRF.Engine.Mom.BoundaryMesher.Mesh(problem, new CircuitRF.Engine.Mom.EmMeshSettings(MinCellsAcrossWidth: 48)));
        double c0 = m.CComplex[0, 0].Real, c1 = 0.5 * (m.CComplex[0, 1].Real + m.CComplex[1, 0].Real);
        double l0 = m.L[0, 0], l1 = 0.5 * (m.L[0, 1] + m.L[1, 0]);
        // A symmetric pair: even (1, 1)/√2 and odd (1, −1)/√2, λ = (l0 ± l1)(c0 ± c1), C_m = c0 ± c1.
        double[] lam = [(l0 + l1) * (c0 + c1), (l0 - l1) * (c0 - c1)], cm = [c0 + c1, c0 - c1];
        double r = 1 / Math.Sqrt(2);
        double[,] tv = { { r, r }, { r, -r } };

        for (int k = 0; k < s.Frequencies.Length; k++)
        {
            double f = s.Frequencies[k], w = 2 * Math.PI * f;
            // The reference: Z = Tv · Zmodal · Tvᵀ, block by block, a lossless line of each mode.
            var z = new NumFlat.Mat<Complex>(4, 4);
            for (int md = 0; md < 2; md++)
            {
                double beta = w * Math.Sqrt(lam[md]), zc = beta / (w * cm[md]);
                Complex coth = 1 / Complex.Tan(beta * L) / Complex.ImaginaryOne, csch = 1 / (Complex.ImaginaryOne * Math.Sin(beta * L));
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                    {
                        double tt = tv[i, md] * tv[j, md];
                        z[i, j] += tt * zc * coth; z[2 + i, 2 + j] += tt * zc * coth;
                        z[i, 2 + j] += tt * zc * csch; z[2 + i, j] += tt * zc * csch;
                    }
            }
            var reference = RFNetwork.ZToS(z, Enumerable.Repeat(new Complex(50, 0), 4).ToArray());
            var sk = s.Matrices[k];
            for (int i = 0; i < 4; i++)
            {
                double vswr = RfHelpers.VswrFromGamma(reference[i, i], sk[i, i]);
                Assert.True(vswr <= 1.025, $"VSWR {vswr:F4} at port {i + 1}, {f / 1e9} GHz");
            }
            if (Math.Abs(f - 1e9) > 1) continue;
            // Each mode's own 2-port from the published Z: A = cos βL whatever the reference impedance.
            var zm = RFNetwork.SToZ(sk, Enumerable.Repeat(new Complex(50, 0), 4).ToArray());
            for (int md = 0; md < 2; md++)
            {
                Complex z11 = 0, z21 = 0;
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++) { z11 += tv[i, md] * zm[i, j] * tv[j, md]; z21 += tv[i, md] * zm[2 + i, j] * tv[j, md]; }
                double bl = Math.Acos(Math.Clamp((z11 / z21).Real, -1, 1));
                double eeff = Math.Pow(bl / L * C0 / w, 2), want = lam[md] * C0 * C0;
                // Brief 3 restates 3 %. Palace's own microstrip phase velocity already exceeds it on the even mode: PWL-2's spike read
                // 1.985–2.003 against kernel A's 1.9387, the product 2.006 at 1 GHz (+3.3 %). The owner set the bound at 3.5 %.
                Assert.True(Math.Abs(eeff / want - 1) <= 0.035, $"mode {(md == 0 ? "even" : "odd")} ε_eff {eeff:F4} against {want:F4} at 1 GHz");
            }
        }
        var agreed = System.Text.RegularExpressions.Regex.Matches(notes, @"the standards agreed to ([0-9.eE+-]+)");
        Assert.NotEmpty(agreed);
        foreach (System.Text.RegularExpressions.Match a in agreed)
            Assert.True(double.Parse(a.Groups[1].Value.TrimEnd('.'), System.Globalization.CultureInfo.InvariantCulture) <= 1.5e-2, a.Value);
    }

    private static (int Code, string Out, string Err) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(WallPortTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var o = proc.StandardOutput.ReadToEndAsync();
        var e = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
    }

    /// <summary>R-pwl3-9 — a standard's cache key is stable for the same solve and moves with what changes the answer (its length,
    /// the setup's settings, the solver); its raw S round-trips exactly.</summary>
    [Fact]
    public void AStandardsCacheKeyFollowsItsSolve_AndItsAnswerRoundTrips()
    {
        var (doc, path) = Stripline();
        var problem = Assemble(doc, path).Problem!;
        var (cal, _) = CircuitRF.Design.Em3d.Em3dWallCalibration.Plan(problem, 1e9, 10e9, []);
        var a = cal!.Standards[0].Problem; var b = cal.Standards[1].Problem;
        string k = CircuitRF.Design.Em3d.Em3dWallCalibration.CacheKey(a, "settings", "Palace");
        Assert.Equal(k, CircuitRF.Design.Em3d.Em3dWallCalibration.CacheKey(a, "settings", "Palace"));
        Assert.NotEqual(k, CircuitRF.Design.Em3d.Em3dWallCalibration.CacheKey(b, "settings", "Palace"));
        Assert.NotEqual(k, CircuitRF.Design.Em3d.Em3dWallCalibration.CacheKey(a, "other settings", "Palace"));
        Assert.NotEqual(k, CircuitRF.Design.Em3d.Em3dWallCalibration.CacheKey(a, "settings", "Emerge"));

        var sIn = new[] { new Complex[,] { { new(0.1, -0.2), new(0.9, 0.3) }, { new(0.9, 0.3), new(-0.05, 0.4) } } };
        cal.Capture(0, [5e9], sIn);
        string file = Path.Combine(_root, "cache", k + ".json");
        cal.Save(0, file);
        Assert.True(CircuitRF.Design.Em3d.Em3dWallCalibration.TryLoad(file, out var f, out var sOut));
        Assert.Equal([5e9], f);
        Assert.Equal(sIn[0], sOut[0]);
        Assert.False(CircuitRF.Design.Em3d.Em3dWallCalibration.TryLoad(Path.Combine(_root, "none.json"), out _, out _));
    }

    /// <summary>R-pwl3 — <c>CalibrateWallPorts</c> is written only when off, reads back, and its absence means on.</summary>
    [Fact]
    public void CalibrateWallPorts_IsWrittenOnlyWhenOff_AndReadsBack()
    {
        string on = EmSetupPersistence.Serialize(new EmSetup { Name = "a" });
        Assert.DoesNotContain("CalibrateWallPorts", on);
        Assert.True(EmSetupPersistence.Deserialize(on).CalibrateWallPorts);
        string off = EmSetupPersistence.Serialize(new EmSetup { Name = "a", CalibrateWallPorts = false });
        Assert.Contains("\"CalibrateWallPorts\": false", off);
        Assert.False(EmSetupPersistence.Deserialize(off).CalibrateWallPorts);
    }

    /// <summary>R-pwl3-14 — `explain` says what a run would do with the wall ports, from the calibration's own plan.</summary>
    [Fact]
    public void ExplainShowsEachFacesGroupAndItsStandards()
    {
        var (_, path) = Stripline();
        var (code, output, err) = RunCli("explain", path, "--setup", "Palace");
        Assert.True(code == 0, err);
        Assert.Contains("wall ports", output);
        Assert.Contains("Wall ports on the xmin face (1)", output);
        Assert.Contains("the xmin face's own", output);
        Assert.Contains("3 standard(s) to solve before the structure", output);
    }

    /// <summary>The 3D view's own setup choosing Wall for ports DRAWN as wave ports: the run takes wall ports, the document is not
    /// touched, and a setup that states nothing runs them as drawn. A Wall row on a lumped sheet is refused by name.</summary>
    [Fact]
    public void TwoSetupsOfOneView_RunOneFaceAsWaveAndAsWall_AndABadRowIsRefused()
    {
        void Wave(JsonObject d)
        {
            foreach (var q in d["Ports"]!.AsArray())
            {
                q!["Kind"] = "Wave"; q["Positive"] = "strip"; q["Negative"] = "airbox/zmin";
                // a wave port's region is the whole face, so it meets both planes
                q["Rect"] = new JsonObject { ["Min"] = new JsonArray(-(W / 2 + 10 * W), -B / 2), ["Size"] = new JsonArray(21 * W, B) };
                q["VoltagePath"] = new JsonObject { ["From"] = new JsonArray(0, -B / 2), ["To"] = new JsonArray(0, 0) };
            }
            d["Setups"]![0]!["AirBox"]!["XMin"] = new JsonObject { ["PaddingUm"] = 0, ["Boundary"] = "Absorbing" };
        }
        // One view, two setups: "Palace" runs the ports as drawn, "Palace wall" chooses Wall for both.
        var (view, p0) = Stripline(d =>
        {
            Wave(d);
            var wall = d["Setups"]![0]!.DeepClone().AsObject();
            wall["Name"] = "Palace wall";
            wall["Ports3D"] = new JsonArray(new JsonObject { ["Port"] = 1, ["Kind"] = "Wall" }, new JsonObject { ["Port"] = 2, ["Kind"] = "Wall" });
            d["Setups"]!.AsArray().Add(wall);
        });
        var drawn = Assemble(view, p0);
        Assert.True(drawn.Ok, drawn.Refusal);
        Assert.All(drawn.Problem!.Ports, q => { Assert.Equal(Em3dPortKind.Wave, q.Kind); Assert.Null(q.Wall); });
        var walled = Assemble(view, p0, "Palace wall");
        Assert.True(walled.Ok, walled.Refusal);
        Assert.All(walled.Problem!.Ports, q => { Assert.Equal(Em3dPortKind.Lumped, q.Kind); Assert.NotNull(q.Wall); });
        Assert.All(view.Ports, q => Assert.Equal(Em3dPortKind.Wave, q.Kind));      // the document is the drawing

        var (missing, pm) = Stripline(d => d["Setups"]![0]!["Ports3D"] = new JsonArray(new JsonObject { ["Port"] = 7, ["Kind"] = "Wall" }));
        var none = Assemble(missing, pm);
        Assert.False(none.Ok);
        Assert.Contains("this view has no port 7", none.Refusal);

        var (lumped, p2) = Stripline(d =>
        {
            d["Ports"]!.AsArray().Add(new JsonObject
            {
                ["Number"] = 3, ["Name"] = "P3", ["Kind"] = "Lumped", ["Plane"] = "XZ", ["Offset"] = 0, ["Z0"] = "50",
                ["Rect"] = new JsonObject { ["Min"] = new JsonArray(Len / 2, -B / 2), ["Size"] = new JsonArray(100000, B / 2) },
            });
            d["Setups"]![0]!["Ports3D"] = new JsonArray(new JsonObject { ["Port"] = 3, ["Kind"] = "Wall" });
        });
        var refused = Assemble(lumped, p2);
        Assert.False(refused.Ok);
        Assert.Contains("chooses between Wave and Wall only for a port drawn on a face", refused.Refusal);
    }

    /// <summary>R-pwl3-3 / D6 — two lines on one face farther apart than the coupling clearance are two groups, each calibrated
    /// alone: its own standards, which leave the other group's line out.</summary>
    [Fact]
    public void LinesFarApartOnOneFace_AreSeparateGroups_EachWithoutTheOthersLine()
    {
        const long Far = 8000000;
        var (doc, path) = Stripline(d =>
        {
            d["Objects"]!.AsArray().Add(new JsonObject
            {
                ["$type"] = "Sheet", ["Name"] = "far", ["Material"] = "Perfect", ["Plane"] = "XY", ["Offset"] = 0,
                ["Rect"] = new JsonObject { ["Min"] = new JsonArray(0, Far - W / 2), ["Size"] = new JsonArray(Len, W) },
            });
            d["Ports"]!.AsArray().Add(WallPort(3, 0, Far - W, 2 * W));
            d["Ports"]!.AsArray().Add(WallPort(4, Len, Far - W, 2 * W));
        });
        var problem = Assemble(doc, path).Problem!;
        var notes = new List<string>();
        var (cal, refusal) = CircuitRF.Design.Em3d.Em3dWallCalibration.Plan(problem, 1e9, 10e9, notes);
        Assert.Null(refusal);
        Assert.Contains(notes, n => n.StartsWith("Wall ports on the xmin face (1)", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.StartsWith("Wall ports on the xmin face (3)", StringComparison.Ordinal));
        var labels = cal!.Standards.Select(x => x.Label).ToList();
        Assert.Contains(labels, l => l.Contains("xmin (1)", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("xmin (3)", StringComparison.Ordinal));
        foreach (var std in cal.Standards.Where(x => x.Label.Contains("(1)", StringComparison.Ordinal)))
            Assert.DoesNotContain(std.Problem.Sheets, sh => sh.Name.StartsWith("far", StringComparison.Ordinal));
    }

    /// <summary>R-pwl3-11/12 — fed ideal matched lines in place of solves (the standards and the 20 mm structure, wall to wall, in the
    /// fill's own ε), the calibration gives back that line, its diagnostics read the fill's ε_eff, and nothing is called non-passive.</summary>
    [Fact]
    public void IdealLinesCalibrateToTheLine_AndTheDiagnosticsReadItsEpsEff()
    {
        var (doc, path) = Stripline();
        var problem = Assemble(doc, path).Problem!;
        var (cal, refusal) = CircuitRF.Design.Em3d.Em3dWallCalibration.Plan(problem, 1e9, 10e9, []);
        Assert.Null(refusal);
        double eps = problem.Materials.Single(m => m.Name == "PTFE").Epsr;
        double[] f = [1e9, 5e9, 10e9];
        Complex[,] Line(double lengthM, double hz)
        {
            var t = Complex.Exp(-Complex.ImaginaryOne * 2 * Math.PI * hz * Math.Sqrt(eps) / 2.99792458e8 * lengthM);
            return new Complex[,] { { 0, t }, { t, 0 } };
        }
        for (int i = 0; i < cal!.Standards.Count; i++)
            cal.Capture(i, f, [.. f.Select(hz => Line(cal.Standards[i].LengthM, hz))]);
        var notes = new List<string>();
        var s = cal.Apply(f, [.. f.Select(hz => Line(Len * 1e-9, hz))], problem.Ports, notes);
        for (int k = 0; k < f.Length; k++)
        {
            var want = Line(Len * 1e-9, f[k]);
            Assert.True((s[k][1, 0] - want[1, 0]).Magnitude < 5e-3, $"S21 at {f[k] / 1e9} GHz: {s[k][1, 0]} against {want[1, 0]}");
            Assert.True(s[k][0, 0].Magnitude < 5e-3, $"S11 at {f[k] / 1e9} GHz: {s[k][0, 0].Magnitude}");
        }
        Assert.DoesNotContain(notes, n => n.Contains("not passive", StringComparison.Ordinal));
        var cubes = cal.DiagnosticCubes(f, problem.Ports).ToDictionary(c => c.Name, c => c.Cube);
        Assert.Equal(["WallPortEeff", "WallPortGamma", "WallPortResidual", "WallPortZc"], cubes.Keys.Order());
        Assert.All(cubes["WallPortEeff"].RealValues, e => Assert.Equal(eps, e, 6));
        Assert.Equal("Wall xmin · group 1 of 1 · P1", cubes["WallPortEeff"].Axes[1].Labels![0]);     // R-pwl4-8 — named for a reader
        Assert.Contains(cal.HeaderLines(), l => l.StartsWith("circuitRF-EM 3D wall group xmin: ports 1, scalar", StringComparison.Ordinal));
    }

    /// <summary>R-pwl3-6 / D7 — a conductor meeting a wall-port face without being a port's line is named: the wall shorts it.</summary>
    [Fact]
    public void AConductorMeetingAWallFace_ThatIsNotAPort_IsNamed()
    {
        var (doc, path) = Stripline(d => d["Objects"]!.AsArray().Add(new JsonObject
        {
            ["$type"] = "Sheet", ["Name"] = "stub", ["Material"] = "Perfect", ["Plane"] = "XY", ["Offset"] = 0,
            ["Rect"] = new JsonObject { ["Min"] = new JsonArray(0, 8000000), ["Size"] = new JsonArray(2000000, W) },
        }));
        var said = C3dWallPorts.GroundedByTheWall(Assemble(doc, path).Problem!);
        var line = Assert.Single(said);
        Assert.Contains("'stub' meets the xmin face", line);
        Assert.DoesNotContain("'strip'", line);
    }

    [Fact]
    public void AWallPortOffTheFaces_IsRefusedNamingTheFace()
    {
        var (doc, path) = Stripline(d => d["Ports"]![1]!["Offset"] = Len / 2);
        var built = Assemble(doc, path);
        Assert.False(built.Ok);
        Assert.Contains("not a face of the air box", built.Refusal);
    }

    [Fact]
    public void AFaceWithAWallPortAndAWavePort_IsRefused()
    {
        var (doc, path) = Stripline(d =>
        {
            var wave = WallPort(3, Len, yLo: W + 100000, ySize: 400000);
            wave["Kind"] = "Wave";
            wave["Positive"] = "strip"; wave["Negative"] = "airbox/zmin";
            d["Ports"]!.AsArray().Add(wave);
        });
        var built = Assemble(doc, path);
        Assert.False(built.Ok);
        Assert.Contains("not both", built.Refusal);
    }

    [Fact]
    public void AWallPortOnASolverOtherThanPalace_IsRefusedNamingPalace()
    {
        var (doc, path) = Stripline(d => d["Setups"]![0]!["Solver3D"] = "Emerge");
        var built = Assemble(doc, path);
        Assert.False(built.Ok);
        Assert.Contains("run on Palace in this version", built.Refusal);
    }

    [Fact]
    public void ARectangleOverTwoLines_IsRefusedNamingBoth()
    {
        string dir = FixturePaths.Require("testdata/pwl");
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "coupled_line_L20000um.c3d")))!.AsObject();
        foreach (var o in doc["Objects"]!.AsArray())
            if (((string?)o!["Name"])!.StartsWith("strip", StringComparison.Ordinal)) { o["Rect"]!["Min"]![0] = 0; o["Rect"]!["Size"]![0] = Len; }
        doc["Ports"] = new JsonArray(new JsonObject
        {
            ["Number"] = 1, ["Name"] = "P1", ["Kind"] = "Wall", ["Plane"] = "YZ", ["Offset"] = 0, ["Z0"] = "50",
            ["Rect"] = new JsonObject { ["Min"] = new JsonArray(-3000000, 0), ["Size"] = new JsonArray(6000000, 1000000) },
        });
        File.Copy(Path.Combine(dir, "pwl.ctech"), Path.Combine(_root, "pwl.ctech"), overwrite: true);
        string path = Path.Combine(_root, "pair.c3d");
        File.WriteAllText(path, doc.ToJsonString());
        var built = Assemble(C3dPersistence.LoadFromFile(path), path);
        Assert.False(built.Ok);
        Assert.Contains("2 lines reach", built.Refusal);
        Assert.Contains("'strip_a'", built.Refusal);
        Assert.Contains("'strip_b'", built.Refusal);
    }
}
