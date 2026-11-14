// brief-icp-6 — the CLI names cell instances: --component-ports spellings (paths, X1/*, cell:<name>, --port-at), explain's pin
// check read from the build, check's verdicts and exit codes, a dry run counting and naming cut ports, and the issue's two commands
// on the shipped examples. The CLI runs as a process, as the other verb gates do. ICP-4's planar fixtures; no solve.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class CellPortCliTests(CellPinReferenceTests.Fixture fx, ITestOutputHelper output) : IClassFixture<CellPinReferenceTests.Fixture>
{
    private static readonly string Repo = RepoRoot();

    /// <summary>Gate 1 (R-icp6-1) — every spelling parses into the entry a .cem stating it reads as, --port-at on each; a bad spelling
    /// is refused naming the grammar.</summary>
    [Fact]
    public void EachSpelling_ParsesIntoTheEntriesTheCemReads_AndABadOneIsRefused()
    {
        var parsed = ComponentPortSpellings.Parse("*, Q1,X1/Q3;X1/* @2,cell:Seg", "cell", out string? error);
        Assert.Null(error);
        string json = """
            {"Name":"s","ComponentPorts":[{"Refdes":"*","PortAt":"Cell"},{"Refdes":"Q1","PortAt":"Cell"},{"Refdes":"X1/Q3","PortAt":"Cell"},
             {"Refdes":"X1/*","PortAt":"Cell"},{"Refdes":"@2","PortAt":"Cell"},{"Refdes":"","Cell":"Seg","PortAt":"Cell"}]}
            """;
        Assert.Equal(EmSetupPersistence.Deserialize(json).ComponentPorts, parsed!);

        foreach (string bad in new[] { "X1//Q3", "*/Q3", "cell:", "Q1=2" })
            Assert.Null(ComponentPortSpellings.Parse(bad, null, out _));
        Assert.Null(ComponentPortSpellings.Parse("X1", "side", out _));

        string cem = Cem("bad", fx.SegLine(), []);
        var doc = Json("explain", cem, "--component-ports", "X1//Q3");
        Assert.Equal(1, doc["exitCode"]!.GetValue<int>());
        var refused = Assert.Single(Diagnostics(doc), d => d.Id == "em.component-ports.refused");
        Assert.Contains("'X1//Q3' has an empty path segment.", refused.Message, StringComparison.Ordinal);
        Assert.Contains(ComponentPortSpellings.Grammar, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Gate 2 (R-icp6-2) — explain --json on ICP-4's Tri cut (B and C referenced to E, E only a return) walks the instance and
    /// each pin exactly as the in-process build reports them: the same lines, the same port numbers, the same classes.</summary>
    [Fact]
    public void ExplainJson_OnACut_MatchesTheBuild_FieldForField()
    {
        var entry = new EmComponentPort("X1").WithPins(new Dictionary<string, EmPinOptions>
        {
            ["B"] = new(Reference: "E"), ["C"] = new(Reference: "E"), ["E"] = new(Port: false),
        });
        string cem = Cem("tri", fx.TriLine(), [entry]);
        var (setup, source) = Load(cem);
        var build = EmPlanarComponentPorts.Build(setup, source)!;
        var cut = Assert.Single(EmCutReport.Cuts(build.Ports.Select(p => (p.Refdes, p.CutPart))));
        var expected = EmCutReport.Lines(cut.Cut, cut.Prefix, EmCutReport.PortsOf(build),
                                         k => source.Technology!.Layers.First(l => l.Key == k).Name);

        var walks = Json("explain", cem, "--component-ports")["result"]!["explain"]!["walks"]!.AsArray();
        var got = walks.Where(w => w!["step"]!.GetValue<string>() is EmCutReport.InstanceStep or EmCutReport.PinStep)
                       .Select(w => (w!["step"]!.GetValue<string>(), w["resolved"]!.GetValue<string>())).ToList();
        foreach (var line in got) output.WriteLine($"{line.Item1}: {line.Item2}");
        Assert.Equal(expected, got);
        Assert.Equal([EmPinClass.Routed, EmPinClass.Routed, EmPinClass.Return], cut.Cut.Pins.Select(p => p.Class));
        foreach (var p in build.Ports.Where(p => p.Ok))
            Assert.Contains(got, l => l.Item2.Contains($"port {p.Number} '{p.Name}', referenced to pin E", StringComparison.Ordinal));
        Assert.Contains(got, l => l.Item2.StartsWith("X1.E: a return,", StringComparison.Ordinal) && l.Item2.EndsWith("X1.B and X1.C return through it", StringComparison.Ordinal));
    }

    /// <summary>Gate 3 (R-icp6-3) — check: a cut that builds exits 0 with its instance at info; an entry left out exits 0 with a warning
    /// (here given on the command line); a list that cannot be built at all exits 1.</summary>
    [Fact]
    public void Check_ExitsZeroWhenBuilt_ZeroWithAWarningWhenLeftOut_AndOneWhenRefused()
    {
        string cem = Cem("built", fx.SegLine(), [new EmComponentPort("X1")]);
        var built = Json("check", cem);
        Assert.Equal(0, built["exitCode"]!.GetValue<int>());
        Assert.Contains(Diagnostics(built), d => d.Id == "check.cem.component-port" && d.Message.Contains("X1 (Seg), selected by name", StringComparison.Ordinal));

        var leftOut = Json("check", cem, "--component-ports", "X1,X9");
        Assert.Equal(0, leftOut["exitCode"]!.GetValue<int>());
        Assert.Contains(Diagnostics(leftOut), d => d.Id == "check.cem.component-port-warning" && d.Message.Contains("X9", StringComparison.Ordinal));

        var bare = fx.SegLine();
        bare.Shapes.RemoveAll(s => s is LabelShape);
        string none = Cem("refused", bare, [new EmComponentPort("X1", Reference: EmComponentPort.CommonReference)]);
        var refused = Json("check", none);
        Assert.Equal(1, refused["exitCode"]!.GetValue<int>());
        Assert.Contains(Diagnostics(refused), d => d.Id == "check.cem.component-port-refused");
    }

    /// <summary>R-icp6-4 — em --dry-run with a cut names its ports and estimates the problem the run would solve: the instance left
    /// out and a port at each face (here more unknowns than the uncut line, which needs no port refinement at the faces).</summary>
    [Fact]
    public void ADryRun_NamesTheCutPorts_AndCountsTheCutProblem()
    {
        string cem = Cem("dry", fx.SegLine(), []);
        var (_, plain, _) = CliProcess.Run(Repo, [], "em", cem, "--dry-run");
        var (exit, cutOut, err) = CliProcess.Run(Repo, [], "em", cem, "--dry-run", "--component-ports", "X1");
        output.WriteLine(cutOut + err);
        Assert.True(exit == 0, err + cutOut);
        Assert.Contains("X1.A: component port 3, an Uncalibrated Port", cutOut, StringComparison.Ordinal);
        Assert.Contains("X1.B: component port 4, an Uncalibrated Port", cutOut, StringComparison.Ordinal);
        var (setup, source) = Load(cem);
        setup.ComponentPorts = [new EmComponentPort("X1")];
        int expected = EmRunService.Preflight(setup, source).PlanarMesh!.UnknownCount;
        Assert.Equal(expected, Unknowns(cutOut));
        Assert.NotEqual(Unknowns(plain), Unknowns(cutOut));

        static int Unknowns(string stdout) => int.Parse(Regex.Match(stdout, @"Estimate:\s+Full-wave planar, ([\d,]+) unknowns").Groups[1].Value.Replace(",", ""));
    }

    /// <summary>Gate 4 (R-icp6-6), the board half — the Filter Board's explain with "*" walks its parts as the build describes them,
    /// with no cell instance or pin step added.</summary>
    [Fact]
    public void TheFilterBoard_ExplainsAsItDid()
    {
        string cem = Path.Combine(Repo, "examples", "PCB Component Ports", "Filter Board", "em", "Filter Board Planar.cem");
        var (setup, source) = Load(cem);
        setup.ComponentPorts = [new EmComponentPort(EmSetup.AllComponents)];
        var expected = EmPlanarComponentPorts.Build(setup, source)!.Ports.Select(p => EmPlanarComponentPorts.Describe(p, source)).ToList();

        var walks = Json("explain", cem, "--component-ports", "*")["result"]!["explain"]!["walks"]!.AsArray();
        Assert.Equal(expected, walks.Where(w => w!["step"]!.GetValue<string>() == "component port").Select(w => w!["resolved"]!.GetValue<string>()));
        Assert.DoesNotContain(walks, w => w!["step"]!.GetValue<string>() is EmCutReport.InstanceStep or EmCutReport.PinStep);
    }

    /// <summary>Gate 4 (R-icp6-6), the chip half — the SpiralResonator: "*" places no device and names both PCells; "@1" cuts the MIM
    /// cap out with pin 2's port and pin 1 floating (the layout's own port 2 sits at that face).</summary>
    [PythonFact]
    public void TheSpiralResonator_NamesItsPCells_AndCutsTheMimCapOut()
    {
        string cem = Path.Combine(Repo, "examples", "PDK PCells", "SpiralResonator", "em", "SpiralResonator.cem");
        var (_, star, starErr) = CliProcess.Run(Repo, [], "explain", cem, "--component-ports", "*");
        output.WriteLine(star + starErr);
        Assert.Contains("no part placed on this layout is a two-pad part or a device; @0 (KIT_SPIRAL), @1 (KIT_MIMCAP) are copper", star, StringComparison.Ordinal);

        var walks = Json("explain", cem, "--component-ports", "@1")["result"]!["explain"]!["walks"]!.AsArray()
            .Select(w => (Step: w!["step"]!.GetValue<string>(), Text: w["resolved"]!.GetValue<string>())).ToList();
        Assert.Contains(walks, w => w.Step == EmCutReport.InstanceStep && w.Text.StartsWith("@1 (KIT_MIMCAP), selected by name", StringComparison.Ordinal));
        Assert.Contains(walks, w => w.Step == EmCutReport.PinStep && w.Text.StartsWith("@1.2: routed", StringComparison.Ordinal) && w.Text.Contains("port 3 '@1.2'", StringComparison.Ordinal));
        Assert.Contains(walks, w => w.Step == EmCutReport.PinStep && w.Text.StartsWith("@1.1: floating", StringComparison.Ordinal));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The layout saved as a cell of the fixture's planar workspace and a .cem beside it naming it, with these entries.</summary>
    private string Cem(string name, LayoutView view, IReadOnlyList<EmComponentPort> entries)
    {
        var source = fx.Source(name, view);
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(source.AbsolutePath)))!;
        var setup = fx.Setup(name, entries);
        setup.LayoutRef = Path.GetRelativePath(ws, source.AbsolutePath).Replace('\\', '/');
        string path = Path.Combine(ws, name + ".cem");
        EmSetupPersistence.SaveToFile(path, setup);
        return path;
    }

    private static (EmSetup Setup, EmLayoutSource Source) Load(string cem)
    {
        var setup = EmSetupPersistence.LoadFromFile(cem);
        string? cws = DocumentKinds.AncestorCws(cem);
        var resolved = EmSetupResolver.Resolve(cem, setup.LayoutRef, cws, new TechnologyCache());
        return (setup, resolved.Source!);
    }

    private JsonNode Json(params string[] args)
    {
        var (_, stdout, stderr) = CliProcess.Run(Repo, [], [.. args, "--json"]);
        output.WriteLine(stderr);
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
