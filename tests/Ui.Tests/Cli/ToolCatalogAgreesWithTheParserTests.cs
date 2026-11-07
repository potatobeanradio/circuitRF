// ================================================================
//  ToolCatalogAgreesWithTheParserTests.cs — brief EMB-5 R-emb5-6.
//
//  The MCP `create` tool documented three views while `new cell --views` took four (36ae6a845's F4): an agent
//  told by the schema that `3d` did not exist never asked for it. A tool option that documents a value list is
//  a second copy of the parser's list, and nothing held them together. These gates do, in both directions, for
//  each documented list: every value the description names is accepted by the CLI verb behind it (asked of the
//  real verb, through the server), and every value the parser accepts is named in the description. A new view,
//  unit, device, technology or analysis that reaches one side only fails a build.
// ================================================================

using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Cli.Serve;
using CircuitRF.Design.Em3d;

namespace CircuitRF.Ui.Tests.Cli;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ToolCatalogAgreesWithTheParserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emb5-cat-" + Guid.NewGuid().ToString("N")[..10]);

    public ToolCatalogAgreesWithTheParserTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary><c>create what=cell views</c> against <c>new cell --views</c>.</summary>
    [Fact]
    public void TheViewsCreateDocuments_AreTheViewsNewCellTakes()
    {
        var documented = Documented("create", "cell", "views", after: "Which views to create: ", until: ". Default");
        Assert.Equal(Sorted(Authoring.ViewWords.Keys), Sorted(documented));

        using var server = new InProcessServe(_root);
        Assert.Equal(0, ExitOf(server.Call("create", new JsonObject { ["what"] = "workspace", ["path"] = _root, ["name"] = "ws" })));
        string ws = Path.Combine(_root, "ws");

        int n = 0;
        foreach (string view in documented)
            Assert.True(0 == ExitOf(server.Call("create", new JsonObject
            {
                ["what"] = "cell", ["workspace"] = ws, ["name"] = $"c{n++}", ["views"] = new JsonArray(view),
            })), $"new cell refused the documented view '{view}'");

        var refused = server.Call("create", new JsonObject { ["what"] = "cell", ["workspace"] = ws, ["name"] = "bad", ["views"] = new JsonArray("schemtic") });
        Assert.Equal(1, ExitOf(refused));
    }

    /// <summary><c>create what=workspace lengthUnit</c> against <c>new workspace --length-unit</c>.</summary>
    [Fact]
    public void TheLengthUnitsCreateDocuments_AreTheOnesNewWorkspaceTakes()
    {
        var documented = Documented("create", "workspace", "lengthUnit", after: "", until: ":");
        Assert.Equal(Sorted(Authoring.LengthUnitWords.Keys), Sorted(documented));

        using var server = new InProcessServe(_root);
        foreach (string unit in documented)
            Assert.True(0 == ExitOf(server.Call("create", new JsonObject
            {
                ["what"] = "workspace", ["path"] = _root, ["name"] = "ws-" + unit, ["lengthUnit"] = unit,
            })), $"new workspace refused the documented length unit '{unit}'");
    }

    /// <summary>The technologies <c>reference technologies</c> lists — which is where <c>create</c>'s <c>tech</c>
    /// points a caller — against the ones <c>new workspace --tech</c> accepts, as its own refusal lists them.</summary>
    [Fact]
    public void TheTechnologiesReferenceLists_AreTheOnesNewWorkspaceAccepts()
    {
        using var server = new InProcessServe(_root);
        var listed = JsonNode.Parse(server.Call("reference", new JsonObject { ["topic"] = "technologies" }))!
            ["result"]!["reference"]!["technologies"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToList();

        var refusal = JsonNode.Parse(server.Call("create", new JsonObject
        {
            ["what"] = "workspace", ["path"] = _root, ["name"] = "nope", ["tech"] = "no-such-technology",
        }))!["diagnostics"]!.AsArray().First(d => d!["id"]!.GetValue<string>() == "new.tech.unknown")!;
        var accepted = refusal["arguments"]!["known"]!.GetValue<string>()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(Sorted(accepted), Sorted(listed));
        Assert.Contains("none", Description("create", "workspace", "tech"));
    }

    /// <summary><c>run analysis=em device</c> and <c>explain device</c> against the parser both verbs share.</summary>
    [Theory]
    [InlineData("run", "em")]
    [InlineData("explain", "")]
    public void TheDevicesDocumented_AreTheDevicesTheParserTakes(string tool, string mode)
    {
        string description = Description(tool, mode, "device");
        var documented = Words(description[(description.IndexOf("auto", StringComparison.Ordinal))..]
                               .Split('.')[0]);
        Assert.Equal(Sorted(Enum.GetNames<EmergeDevice>().Select(n => n.ToLowerInvariant())), Sorted(documented));
        foreach (string d in documented) Assert.NotNull(EmergeDevices.Parse(d));
    }

    /// <summary><c>run</c>'s analysis kinds against the CLI: every mode is a verb the CLI dispatches, a <c>yield</c>
    /// mode names a noun the verb takes, and every noun but <c>trial</c> (which is <c>trial=</c> on mc and yield) is a
    /// mode.</summary>
    [Fact]
    public void TheAnalysisKindsRunDocuments_AreTheCliVerbsAndNouns()
    {
        var run = ToolCatalog.Tools.Single(t => t.Name == "run");
        var nouns = CircuitRF.Cli.Yield.Nouns.Select(n => n.Noun).ToHashSet(StringComparer.Ordinal);

        foreach (var mode in run.Modes)
        {
            Assert.True(CliEntry.IsVerb(mode.Verb[0]), $"run analysis={mode.Selector} names '{mode.Verb[0]}', which the CLI does not dispatch");
            if (mode.Verb[0] == "yield")
                Assert.Contains(mode.Verb[1], nouns);
        }

        var reached = run.Modes.Where(m => m.Verb[0] == "yield").Select(m => m.Verb[1]).ToHashSet(StringComparer.Ordinal);
        foreach (string noun in nouns.Where(n => n != "trial"))
            Assert.Contains(noun, reached);
        Assert.Contains(run.Modes.Single(m => m.Selector == "yield").Options, o => o.Json == "trial");
    }

    // ── the documented list, read from the description a client is shown ─────────────────────────

    private static string Description(string tool, string mode, string option)
    {
        var spec = ToolCatalog.Tools.Single(t => t.Name == tool);
        var m = spec.Modes.Single(x => x.Selector == mode);
        return m.Options.Single(o => o.Json == option).Description;
    }

    /// <summary>What the plot verb takes, the plot tool offers: an antenna pattern needs radial=db, and with no way to ask for it
    /// over the server a pattern drew an empty disc. Read from the parser so a new flag cannot reach one side only.</summary>
    [Fact]
    public void EveryPlotFlagIsOnThePlotTool_AndAPatternPlotsThroughIt()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "PlotVerb.cs"));
        var flags = System.Text.RegularExpressions.Regex.Matches(source, "case \"(--[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("--radial", flags);
        var offered = ToolCatalog.Tools.Single(t => t.Name == "plot").Modes.SelectMany(m => m.Options).Select(o => o.Cli).ToHashSet();
        Assert.Empty(flags.Where(f => !offered.Contains(f)));

        string npy = Path.Combine(_root, "run.npy"), png = Path.Combine(_root, "pattern.png");
        RfCore.Export.DataSetExporter.Export(CircuitRF.Ui.Tests.DataDisplay.PatternFixture.Data, npy, RfCore.Export.ExportFormat.Npy);
        using var server = new InProcessServe(_root);
        string plotted = server.Call("plot", new JsonObject
        {
            ["path"] = npy, ["output"] = png, ["type"] = "polar", ["radial"] = "db", ["dbUnit"] = "dB(W/sr)", ["legend"] = "below",
            ["trace"] = new JsonArray("cube=farfield.U,cut=0,port=1,y=db10"),
        });
        Assert.True(ExitOf(plotted) == 0, plotted);
        Assert.True(new FileInfo(png).Length > 0);
    }

    /// <summary>An agent chooses cores against what the machine has, and is told what they cost in memory.</summary>
    [Fact]
    public void SolverReportsTheMachinesCores_AndADryRunReadsCores()
    {
        using var server = new InProcessServe(_root);
        var machine = JsonNode.Parse(server.Call("solver", new JsonObject()))!["result"]!["machine"]!;
        int physical = machine["physicalCores"]!.GetValue<int>();
        Assert.Equal(CircuitRF.Engine.Em3d.PhysicalCores.Count, physical);
        Assert.Contains("more memory", machine["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(machine["memoryGB"]!.GetValue<double>() > 0);

        string shipped = Path.Combine(RepoRoot(), "examples", "3D Eigenmode", "Cavity", "3d", "Cavity.c3d");
        string c3d = Path.Combine(_root, "Cavity.c3d");
        File.Copy(shipped, c3d);
        JsonNode? Em3d(int cores) => JsonNode.Parse(server.Call("explain", new JsonObject { ["path"] = c3d, ["cores"] = cores }))!["result"]!["explain"]!["em3d"];
        Assert.Equal(1, Em3d(1)!["device"]!["processes"]!.GetValue<int>());
        Assert.Contains(Em3d(physical + 1)!["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("physical", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) return d.FullName;
        throw new InvalidOperationException("no repo root above the test binaries");
    }

    /// <summary>The values a description names between two markers: parenthetical asides dropped, then split on commas
    /// and "or".</summary>
    private static IReadOnlyList<string> Documented(string tool, string mode, string option, string after, string until)
    {
        string d = Description(tool, mode, option);
        int start = d.IndexOf(after, StringComparison.Ordinal) + after.Length;
        int end = d.IndexOf(until, start, StringComparison.Ordinal);
        return Words(d[start..end]);
    }

    private static IReadOnlyList<string> Words(string list)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        foreach (char c in list)
        {
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        return [.. sb.ToString().Replace(" or ", ",", StringComparison.Ordinal)
                   .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static List<string> Sorted(IEnumerable<string> s) => [.. s.OrderBy(x => x, StringComparer.Ordinal)];

    private static int ExitOf(string document)
        => JsonNode.Parse(document)!["exitCode"]!.GetValue<int>();
}
