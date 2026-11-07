using System.Text.Json.Nodes;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// brief-lcp-2 — the CLI accepts a layout's planar .cem with component ports: explain and check report what the in-process builder
// builds, em --dry-run applies a list and writes nothing, em runs it and names the parts, and a thermal setup is refused by the
// kept id. LCP-1's board (two drawn ports, C1 and C2 across two gaps) and its 2 s run are the only solve.
public sealed class PlanarComponentPortCliTests(PlanarComponentPortTests.Board board, Xunit.Abstractions.ITestOutputHelper output)
    : IClassFixture<PlanarComponentPortTests.Board>
{
    private static readonly string Repo = RepoRoot();

    /// <summary>Gate 1 — explain and check on the .cem report the parts EmPlanarComponentPorts.Build reports, line for line.</summary>
    [Fact]
    public void ExplainAndCheck_ReportTheBuildersParts()
    {
        string cem = Cem("named", ["C1", "C2"]);
        var build = EmPlanarComponentPorts.Build(PlanarComponentPortTests.Setup(["C1", "C2"]), board.Source())!;
        var expected = build.Ports.Select(p => EmPlanarComponentPorts.Describe(p, board.Source())).ToList();
        Assert.Equal(2, expected.Count);
        Assert.All(expected, e => Assert.Contains("a delta gap across a strip", e));

        var explain = Json("explain", cem, "--component-ports");
        var lines = explain["result"]!["explain"]!["walks"]!.AsArray()
            .Where(w => w!["step"]!.GetValue<string>() == "component port").Select(w => w!["resolved"]!.GetValue<string>()).ToList();
        Assert.Equal(expected, lines);

        var check = Json("check", cem);
        var ports = Diagnostics(check).Where(d => d.Id == "check.cem.component-port").Select(d => d.Message).ToList();
        Assert.Equal(2, ports.Count);
        for (int i = 0; i < 2; i++) Assert.EndsWith(expected[i], ports[i]);
        Assert.Equal(board.ClayBytes, File.ReadAllBytes(board.ClayPath));
    }

    /// <summary>Gate 2 — a dry run with a list reports the ports and writes nothing to the .cem, the .clay or the results.</summary>
    [Fact]
    public void ADryRun_ReportsThePorts_AndWritesNothing()
    {
        string cem = Cem("dry", []);
        byte[] before = File.ReadAllBytes(cem);
        var (exit, stdout, stderr) = CliProcess.Run(Repo, [], "em", cem, "--dry-run", "--component-ports", "C1,C2");
        output.WriteLine(stdout + stderr);
        Assert.True(exit == 0, stderr + stdout);
        Assert.Contains("C1: component port 3, a delta gap across a strip", stdout);
        Assert.Contains("C2: component port 4, a delta gap across a strip", stdout);
        Assert.Equal(before, File.ReadAllBytes(cem));
        Assert.Equal(board.ClayBytes, File.ReadAllBytes(board.ClayPath));
        string results = Path.Combine(board.Workspace, "results");
        Assert.False(Directory.Exists(results) && Directory.GetFiles(results, "dry*").Length > 0);
    }

    /// <summary>Gate 3 — em --component-ports runs the .cem and the Touchstone names the parts.</summary>
    [Fact]
    public void ARun_WritesTheTouchstone_WithThePartNames()
    {
        string cem = Cem("run", []);
        byte[] before = File.ReadAllBytes(cem);
        string snp = Path.Combine(Directory.CreateDirectory(board.Results("cli")).FullName, "run.s4p");
        var (exit, stdout, stderr) = CliProcess.Run(Repo, [], "em", cem, "--component-ports", "C1,C2", "-o", snp);
        output.WriteLine(stdout + stderr);
        Assert.True(exit == 0, stderr + stdout);
        string text = File.ReadAllText(snp);
        Assert.Contains("! Port[3] = C1", text, StringComparison.Ordinal);
        Assert.Contains("! Port[4] = C2", text, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(cem));
        Assert.Equal(board.ClayBytes, File.ReadAllBytes(board.ClayPath));
    }

    /// <summary>Gate 4 — a thermal setup is refused by the kept id.</summary>
    [Fact]
    public void AThermalSetup_IsRefusedByTheKeptId()
    {
        string cem = Cem("thermal", [], s => s.Problem3D = Em3dProblemType.Thermal);
        var doc = Json("em", cem, "--component-ports", "C1");
        Assert.Equal(1, doc["exitCode"]!.GetValue<int>());
        var refused = Assert.Single(Diagnostics(doc), d => d.Id == "em.component-ports.refused");
        Assert.Contains("thermal", refused.Message);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private string Cem(string name, string[] parts, Action<EmSetup>? edit = null)
    {
        var setup = PlanarComponentPortTests.Setup(parts);
        setup.Name = name;
        setup.LayoutRef = Path.GetRelativePath(board.Workspace, board.ClayPath).Replace('\\', '/');
        edit?.Invoke(setup);
        string path = Path.Combine(board.Workspace, name + ".cem");
        EmSetupPersistence.SaveToFile(path, setup);
        return path;
    }

    private JsonNode Json(params string[] args)
    {
        var (_, stdout, stderr) = CliProcess.Run(Repo, [], [.. args, "--json"]);
        output.WriteLine(stdout + stderr);
        return JsonNode.Parse(stdout)!;
    }

    private static IEnumerable<(string Id, string Message)> Diagnostics(JsonNode doc)
        => doc["diagnostics"]!.AsArray().Select(d => (d!["id"]!.GetValue<string>(), d["message"]!.GetValue<string>()));

    private static string RepoRoot()
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;
        return repo;
    }
}
