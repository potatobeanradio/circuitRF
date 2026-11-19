// PWL-3 R-pwl3-9 — the standards cache, through the run itself: a stand-in solver counts what it is asked to solve. The first run
// solves every standard and keeps each; the second solves none of them. No real solver runs.

using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Em3dLegStandInCollection.Name)]
public sealed class WallStandardsCacheRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pwl3c-" + Guid.NewGuid().ToString("N")[..12]);
    private int _standards, _structures;
    private readonly List<Em3dSolverId> _standardSolvers = [];

    public WallStandardsCacheRunTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Em3dRunService.LegStandIn = null;
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>Captures a standard's raw S (a matched line) as the Palace leg does, and stops a structure's solve on purpose: what
    /// is counted is what the run ASKED to solve.</summary>
    private sealed class Counting(Em3dSolverDescriptor d, WallStandardsCacheRunTests t) : Em3dRunService.SolverLeg(d)
    {
        public override void Configure(Em3dRunService.RunContext run) { }
        public override void Prepare(Em3dProblem problem, Em3dRunService.RunContext run) { }
        public override Em3dRunService.Leg Execute(Em3dProblem problem, string snpBase, Em3dRunService.RunContext run)
        {
            if (run.CaptureRaw is not { } capture)
            {
                t._structures++;
                return Em3dRunService.Leg.Failed(Solver.Id, EmRunStatus.EngineError, EmDiagnostics.SolveFailed("stand-in"));
            }
            t._standards++;
            t._standardSolvers.Add(Solver.Id);
            int n = problem.Ports.Count;
            var s = new Complex[n, n];
            for (int i = 0; i < n; i++) s[i, (i + n / 2) % n] = Complex.FromPolarCoordinates(1, -0.5);
            capture([problem.Frequency.StartHz], [s]);
            return new Em3dRunService.Leg(Solver.Id, EmRunStatus.Ok, null, null, null, null, Solver.DisplayName);
        }
    }

    /// <summary>testdata/pwl's stripline with a wall port at each end, and its Palace setup.</summary>
    private (C3dDocument Doc, string Path, EmSetup Setup) Stripline()
    {
        string dir = FixturePaths.Require("testdata/pwl");
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "stripline_line_L20000um.c3d")))!.AsObject();
        foreach (var o in doc["Objects"]!.AsArray())
            if ((string?)o!["Name"] == "strip") { o["Rect"]!["Min"]![0] = 0; o["Rect"]!["Size"]![0] = 20000000; }
        JsonObject Port(int n, long at) => new()
        {
            ["Number"] = n, ["Name"] = $"P{n}", ["Kind"] = "Wall", ["Plane"] = "YZ", ["Offset"] = at, ["Z0"] = "50",
            ["Rect"] = new JsonObject { ["Min"] = new JsonArray(-1717866, -1000000), ["Size"] = new JsonArray(3435732, 2000000) },
        };
        doc["Ports"] = new JsonArray(Port(1, 0), Port(2, 20000000));
        File.Copy(Path.Combine(dir, "pwl.ctech"), Path.Combine(_root, "pwl.ctech"), overwrite: true);
        string path = Path.Combine(_root, "s.c3d");
        File.WriteAllText(path, doc.ToJsonString());
        var loaded = C3dPersistence.LoadFromFile(path);
        return (loaded, path, C3dSetups.ForRun(C3dSetups.Read(loaded).Single(s => s.Name == "Palace").Setup!, path));
    }

    [Fact]
    public void ASecondRunReusesEveryStandard_AndSolvesOnlyTheStructure()
    {
        var (loaded, path, setup) = Stripline();
        string results = Path.Combine(_root, "results");
        Em3dRunService.LegStandIn = (root, d) => root.StartsWith(_root, StringComparison.Ordinal) ? new Counting(d, this) : null;

        EmRunService.RunThreeDView(setup, loaded, path, null, results);
        Assert.Equal(3, _standards);
        Assert.Equal(1, _structures);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(results, Em3dWallCalibration.CacheFolder), "*.json").Length);

        EmRunService.RunThreeDView(setup, loaded, path, null, results);
        Assert.Equal(3, _standards);        // none solved again
        Assert.Equal(2, _structures);

        // A renamed setup with the same settings solves the same standards: its name is not part of their solve.
        setup.Name = "Palace, renamed";
        EmRunService.RunThreeDView(setup, loaded, path, null, results);
        Assert.Equal(3, _standards);
        Assert.Equal(3, _structures);
    }

    /// <summary>A comparison calibrates each solver's structure with standards THAT solver solved, never with the first solver's:
    /// the gap's discretisation is what the error box removes, and it differs between solvers.</summary>
    [Fact]
    public void AComparison_SolvesEachSolversOwnStandards()
    {
        var (loaded, path, setup) = Stripline();
        setup.Solver3D = Em3dSolverChoice.Compare([Em3dSolverId.Palace, Em3dSolverId.Emerge], Em3dSolverId.Palace);
        Em3dRunService.LegStandIn = (root, d) => root.StartsWith(_root, StringComparison.Ordinal) ? new Counting(d, this) : null;

        EmRunService.RunThreeDView(setup, loaded, path, null, Path.Combine(_root, "results"));
        Assert.Equal(3, _standardSolvers.Count(x => x == Em3dSolverId.Palace));
        Assert.Equal(3, _standardSolvers.Count(x => x == Em3dSolverId.Emerge));
        Assert.Equal(2, _structures);
    }
}
