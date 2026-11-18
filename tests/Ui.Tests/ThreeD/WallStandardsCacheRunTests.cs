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
            int n = problem.Ports.Count;
            var s = new Complex[n, n];
            for (int i = 0; i < n; i++) s[i, (i + n / 2) % n] = Complex.FromPolarCoordinates(1, -0.5);
            capture([problem.Frequency.StartHz], [s]);
            return new Em3dRunService.Leg(Solver.Id, EmRunStatus.Ok, null, null, null, null, Solver.DisplayName);
        }
    }

    [Fact]
    public void ASecondRunReusesEveryStandard_AndSolvesOnlyTheStructure()
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
        File.Copy(Path.Combine(dir, "pwl.ctech"), Path.Combine(_root, "pwl.ctech"));
        string path = Path.Combine(_root, "s.c3d");
        File.WriteAllText(path, doc.ToJsonString());
        var loaded = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(loaded).Single(s => s.Name == "Palace").Setup!, path);
        string results = Path.Combine(_root, "results");
        Em3dRunService.LegStandIn = (root, d) => root.StartsWith(_root, StringComparison.Ordinal) ? new Counting(d, this) : null;

        EmRunService.RunThreeDView(setup, loaded, path, null, results);
        Assert.Equal(3, _standards);
        Assert.Equal(1, _structures);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(results, Em3dWallCalibration.CacheFolder), "*.json").Length);

        EmRunService.RunThreeDView(setup, loaded, path, null, results);
        Assert.Equal(3, _standards);        // none solved again
        Assert.Equal(2, _structures);
    }
}
