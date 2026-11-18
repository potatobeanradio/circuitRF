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

    private static Em3dGenerationResult Assemble(C3dDocument doc, string path)
    {
        var embedded = C3dSetups.Read(doc).Single(s => s.Name == "Palace").Setup!;
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
