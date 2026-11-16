using System.Text.Json.Nodes;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-20 — a hollow guide's wave port of two modes and a wave port's reference plane moved by a stated distance: Hertzon's
/// NumberOfModes and Deembed translated to the <c>.c3d</c>'s Modes and DeembedUm, the problem EMerge is handed, what is refused, and
/// the export. The fixture (<c>two-mode-guide.hzn</c>) was written for these tests by a script from <c>guide-post.hzn</c>: the same
/// closed guide 22.86 × 15 mm with its post, so TE10 and TE01 both propagate from 10 GHz, each end a wave port of two modes, the
/// second's plane moved 5 mm into the guide. The comparisons with the analytic guide are solves, run by hand and recorded in
/// src/Design/RESOLVED.md (HZN-20).
/// </summary>
public sealed class HznMultiModeWavePortTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn20_" + Guid.NewGuid().ToString("N")[..8]);

    public HznMultiModeWavePortTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "two-mode-guide.hzn");

    /// <summary>The fixture with its design's JSON edited, written beside the test.</summary>
    private string Edited(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        edit(json["designs"]![0]!.AsObject());
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6] + ".hzn");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private static C3dTranslation Translate(string path) => HznToC3d.Translate(HznFile.Read(path).File!.Project, "TwoMode");

    private static string Why(C3dTranslation t) => string.Join("; ", t.Refusals.Select(r => r.What + ": " + r.Why));

    private static Em3dProblem Problem(string path)
    {
        var (plan, refusal) = HznEmergeRun.Plan(path, "TwoMode", null);
        Assert.True(plan is not null, refusal?.Render());
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        return made.Problem!;
    }

    /// <summary>R-hzn20-3 — NumberOfModes 2 is Modes 2, a port's modes take consecutive numbers in port order (port 2 starts at 3), and
    /// Deembed True with 5mm is DeembedUm 5000, a distance read and so not listed as unused.</summary>
    [Fact]
    public void NumberOfModesAndDeembed_Translate_ModesNumberedInPortOrder()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, Why(t));
        var ports = t.Document!.Ports.OrderBy(p => p.Number).ToList();
        Assert.Equal([(1, 2, 0.0), (3, 2, 5000.0)], ports.Select(p => (p.Number, p.Modes, p.DeembedUm)));
        Assert.Equal([1, 2, 3, 4], ports.SelectMany(C3dPorts.Numbers));
        Assert.DoesNotContain(t.Provenance!.Unused, u => u.Key == "DeembedDistance");
    }

    /// <summary>R-hzn20-1/2 — two hollow, mode-normalised ports per port sharing its face group: TE10's polarity line across the
    /// narrow side (z) at the broad side's centre, TE01's across the broad side (x) at the narrow side's centre; port 2's modes are
    /// moved 5 mm, and EMerge is handed one wave-port entry per port listing both modes.</summary>
    [Fact]
    public void TheProblem_HasTwoHollowModesPerPort_WithTheirPolarityLines()
    {
        var problem = Problem(Fixture);
        Assert.Empty(problem.Validate());
        var p1 = problem.Ports.Where(p => p.FaceGroup == 1).OrderBy(p => p.Number).ToList();
        Assert.Equal([(1, "TE10"), (2, "TE01")], p1.Select(p => (p.Number, p.HollowMode!.Name)));
        Assert.All(problem.Ports, p => Assert.True(p.Hollow && p.ModeNormalised && !p.IsTerminal));
        Assert.Equal(["P1_M1", "P1_M2"], p1.Select(Em3dPortMap.Label));
        var (te10, te01) = (p1[0].VoltagePath!.Value, p1[1].VoltagePath!.Value);
        Assert.Equal(15e-3, te10.To.Z - te10.From.Z, 9);
        Assert.Equal(te10.From.X, te10.To.X, 12);
        Assert.Equal(22.86e-3, te01.To.X - te01.From.X, 9);
        Assert.Equal(te01.From.Z, te01.To.Z, 12);
        Assert.All(problem.Ports.Where(p => p.FaceGroup == 3), p => Assert.Equal(5e-3, p.ReferencePlane.ShiftM, 12));

        var lowering = EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(null));
        Assert.Null(lowering.Refusal);
        var waves = JsonNode.Parse(lowering.ProblemJson!)!["wavePorts"]!.AsArray();
        Assert.Equal("[[1,2],[3,4]]", new JsonArray([.. waves.Select(w => new JsonArray([.. w!["hollowModes"]!.AsArray().Select(m => m!["number"]!.DeepClone())]))]).ToJsonString());
    }

    /// <summary>R-hzn20-1 — a square opening (TE10 and TE01 degenerate) and three modes are each refused by name; a sweep point below
    /// the second mode's cutoff is refused naming that mode.</summary>
    [Fact]
    public void ASquareOpening_ThreeModes_AndAPointBelowTheSecondCutoff_AreRefused()
    {
        var square = Translate(Edited(d => d["variables"]!["wb"] = "22.86mm"));
        Assert.Contains(square.Refusals, r => r.Why.Contains("is square") && r.Why.Contains("degenerate"));
        var three = Translate(Edited(d => { foreach (var x in d["excitations"]!.AsArray()) x!["props"]!["NumberOfModes"] = "3"; }));
        Assert.Contains(three.Refusals, r => r.Why.Contains("at most two modes"));
        var low = Problem(Edited(d => d["setups"]![0]!["sweeps"]![0]!["props"]!["Start"] = "9GHz"));
        Assert.Contains("mode 2 (TE01)", Em3dModePorts.BelowCutoffRefusal(low));
    }

    /// <summary>R-hzn20-1/2 — Palace and openEMS refuse a port of several modes by name; a lumped port with DeembedUm is a refusal
    /// check reports.</summary>
    [Fact]
    public void OtherSolversRefuseSeveralModes_AndALumpedPortRefusesDeembedding()
    {
        var problem = Problem(Fixture);
        Assert.Contains("which circuitRF runs on EMerge only", Em3dModePorts.SolverRefusal(problem, PalaceSolver.Id));
        Assert.Contains("which circuitRF runs on EMerge only", Em3dModePorts.SolverRefusal(problem, OpenEmsSolver.Id));
        Assert.Null(Em3dModePorts.SolverRefusal(problem, EmergeSolver.Id));

        var t = Translate(Fixture);
        var doc = t.Document!;
        var lumped = doc.Ports.First();
        (lumped.Kind, lumped.Modes, lumped.Z0) = (Em3dPortKind.Lumped, 1, "50");
        lumped.DeembedUm = 100;
        var e = new C3dElaborator(kernel: null) { AttachedTechnology = t.Technology }.Elaborate(doc, Path.Combine(_root, "d.c3d"), null);
        Assert.Contains(C3dPortReports.For(doc, e), r => r.Result.Refusal?.Contains("is a lumped port with DeembedUm") == true);
    }

    /// <summary>R-hzn20-3 — export writes NumberOfModes and the de-embedding back, Hertzon's port numbers in port order, and the file
    /// reads back as it was.</summary>
    [Fact]
    public void Export_WritesNumberOfModesAndDeembedding_AndReadsBack()
    {
        var t = Translate(Fixture);
        var written = C3dToHzn.Write(t.Document!, new HznExportOptions { DesignName = "TwoMode", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("TwoMode", written.Refusals).Render());
        string path = Path.Combine(_root, "native.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        var props = new HznDesign(HznFile.Read(path).File!.Project.Designs.Single().Json).Excitations.Select(x => x.Props).ToList();
        Assert.Equal([("1", "2", "False"), ("2", "2", "True")], props.Select(p => (p["PortNumber"], p["NumberOfModes"], p["Deembed"])));
        Assert.Equal("5mm", props[1]["DeembedDistance"]);
        var back = Translate(path);
        Assert.True(back.Ok, Why(back));
        Assert.Equal([(1, 2, 0.0), (3, 2, 5000.0)], back.Document!.Ports.OrderBy(p => p.Number).Select(p => (p.Number, p.Modes, p.DeembedUm)));
    }
}
