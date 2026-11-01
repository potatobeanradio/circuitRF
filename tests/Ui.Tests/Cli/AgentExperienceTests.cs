// EMB-5 part B (R-emb5-7 … R-emb5-14) — what an agent driving circuitRF over the protocol or the command line reads back:
// a duration read the way its refusal spells it, a small form for every long result, a refusal that does not flood, a
// followed curve that is a trace and can become a Touchstone, a schematic reference that expands every type it names, a
// vendor part placed from a .cnl, and a .cdd written headlessly that opens with its traces. One test per claim; every
// picture and every library is generated here, and nothing is timed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.Tests.PlotRecognition;
using CircuitRF.Ui.Tests.VendorLibraries;
using Xunit;
using Xunit.Abstractions;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;

namespace CircuitRF.Ui.Tests.Cli;

internal static class Agent
{
    public static string Dir(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-emb5-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static JsonNode Doc(string stdout) => JsonNode.Parse(stdout)!;

    public static IEnumerable<string> Ids(JsonNode doc)
        => doc["diagnostics"]!.AsArray().Select(d => d!["id"]!.GetValue<string>());

    public static string Text(JsonObject frame) => frame["result"]!["content"]![0]!["text"]!.GetValue<string>();
}

/// <summary>R-emb5-7: one reader of a wall-clock limit, taking every spelling its refusal offers.</summary>
public sealed class DurationSpellingTests
{
    [Theory]
    [InlineData("10min", 600)]
    [InlineData("10 min", 600)]
    [InlineData("600", 600)]
    [InlineData("600s", 600)]
    [InlineData("600000ms", 600)]
    [InlineData("0.5h", 1800)]
    [InlineData("90 s", 90)]
    public void EverySpelling_ReadsTheSameSeconds(string text, double seconds)
        => Assert.Equal(seconds, TuningValidator.TimeLimitSeconds(text)!.Value, 9);

    /// <summary>The CLI's --time, the MCP time argument and a timelimit= line all take 10min; a refusal quotes it; and
    /// the same call's result=summary is the small form (R-emb5-8) — accepted rather than refused.</summary>
    [Fact]
    public void TenMinutes_IsTakenByTheCli_TheMcp_AndTheLine_AndARefusalQuotesIt()
    {
        string root = Agent.Dir("time");
        string onLine = OptCli.Write(root, "line.cnl", OptCircuits.LSection("lm", "maxiter=300 timelimit=10min"));
        var check = OptCli.Run("check", onLine, "--json");
        Assert.True(check.Exit == 0, check.StdOut);
        Assert.DoesNotContain("tuning.optimize.timelimit", Agent.Ids(Agent.Doc(check.StdOut)));
        Assert.Equal(0, OptCli.Run("opt", onLine).Exit);

        string cnl = OptCli.Write(root, "lsec.cnl", OptCircuits.LSection("lm", "maxiter=300"));
        Assert.Equal(0, OptCli.Run("opt", cnl, "--time", "10min").Exit);
        var refused = OptCli.Run("opt", cnl, "--time", "10 minutes");
        Assert.Equal(1, refused.Exit);
        Assert.Contains("10min", refused.StdErr, StringComparison.Ordinal);

        using var server = new OptMcpTests.Serve(root);
        server.Request("initialize", new JsonObject());
        var doc = Agent.Doc(Agent.Text(server.Request("tools/call", new JsonObject
        {
            ["name"] = "run",
            ["arguments"] = new JsonObject { ["analysis"] = "optimize", ["path"] = cnl, ["time"] = "10min", ["result"] = "summary" },
        })));
        Assert.Equal(0, doc["exitCode"]!.GetValue<int>());
        Assert.DoesNotContain("serve.args.not-for-mode", Agent.Ids(doc));
        var optimize = doc["result"]!["optimize"]!;
        Assert.Null(optimize["perIteration"]);
        Assert.All(optimize["variables"]!.AsArray(), v => Assert.False(string.IsNullOrEmpty(v!["best"]!.GetValue<string>())));
        Assert.All(optimize["goals"]!.AsArray(), g => Assert.NotNull(g!["margin"]));
        Assert.NotNull(doc["result"]!["summarized"]);
    }
}

/// <summary>R-emb5-9: a refused plot reading of a picture holding hundreds of fragments answers with the count and the
/// longest few, its refusal first, under a fixed size; --all-traces lists them all.</summary>
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class RefusedReadingSizeTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    [Fact]
    public void ARefusedReadingOfHundredsOfFragments_IsBounded_AndLeadsWithItsRefusal()
    {
        string png = Noisy(pictures.Picture("s21lowpass"), Path.Combine(pictures.Dir, "emb5-noisy.png"));
        var bounded = CliProcess.Run(PalaceBackendRoot(), [], "recognize", png, "--json");
        var all     = CliProcess.Run(PalaceBackendRoot(), [], "recognize", png, "--json", "--all-traces");
        output.WriteLine($"bounded {bounded.StdOut.Length} chars, all {all.StdOut.Length} chars");
        Assert.Equal(1, bounded.ExitCode);

        var plots = Agent.Doc(bounded.StdOut)["result"]!["recognize"]!["plots"]!.AsObject();
        Assert.Equal("refusal", plots.First().Key);
        Assert.Contains("--manual-box", plots["refusal"]!["flag"]!.GetValue<string>(), StringComparison.Ordinal);
        int read = plots["tracesRead"]!.GetValue<int>();
        output.WriteLine($"{read} traces read");
        // 300 dashes read as a few dozen traces (near colours join); far more than the five the bounded form lists.
        Assert.True(read > 30, $"the fixture made only {read} traces");
        Assert.Equal(5, plots["traces"]!.AsArray().Count);
        Assert.InRange(bounded.StdOut.Length, 0, 20_000);

        var every = Agent.Doc(all.StdOut)["result"]!["recognize"]!["plots"]!;
        Assert.Equal(read, every["traces"]!.AsArray().Count);
        Assert.Null(every["tracesRead"]);
    }

    private static string PalaceBackendRoot() => CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot();

    /// <summary>The plot with its y axis's tick labels painted out (nothing calibrates that axis) and 300 short dashes in 150 colours
    /// scattered over the plot area: an instrument screenshot's noise, generated.</summary>
    private static string Noisy(string source, string target)
    {
        var raster = ImageSource.FromFile(source).Source!.Raster;
        var frame  = PlotReading.FindPlots(ImageSource.FromFile(source).Source!).Single();
        var rgba   = (byte[])raster.Rgba.Clone();
        void Set(int x, int y, int rgb)
        {
            if (x < 0 || y < 0 || x >= raster.Width || y >= raster.Height) return;
            int i = 4 * (y * raster.Width + x);
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }
        int bg = raster.Rgb(1, 1);
        for (int y = 0; y < raster.Height; y++)
            for (int x = 0; x < raster.Width; x++)
                if (x < frame.Left - 1) Set(x, y, bg);

        var rng = new Random(5);
        for (int k = 0; k < 300; k++)
        {
            double hue = (k % 150) / 150.0 * 6;
            int c = (int)hue, f = (int)((hue - c) * 255);
            int rgb = c switch
            {
                0 => 0xFF0000 | f << 8, 1 => (255 - f) << 16 | 0xFF00, 2 => 0x00FF00 | f,
                3 => (255 - f) << 8 | 0xFF, 4 => f << 16 | 0xFF, _ => 0xFF0000 | (255 - f),
            };
            int x0 = rng.Next((int)frame.Left + 4, (int)frame.Right - 14), y0 = rng.Next((int)frame.Top + 4, (int)frame.Bottom - 4);
            // On blank paper only: a dash across a gridline would break the frame the reading finds.
            for (int dx = 0; dx < 8; dx++)
                for (int dy = 0; dy < 2; dy++)
                    if (raster.Rgb(x0 + dx, y0 + dy) == bg) Set(x0 + dx, y0 + dy, rgb);
        }
        File.WriteAllBytes(target, new RasterImage(raster.Width, raster.Height, rgba).EncodePng());
        return target;
    }
}

/// <summary>R-emb5-10/11: a click between a coloured trace and a black gridline follows the trace and names the
/// gridline; a followed curve given is= becomes a Touchstone.</summary>
public sealed class FollowedCurveTests
{
    // A 600 x 400 picture: the plot area 100..500 x 50..350, a black 0 dB gridline across it at y = 150, and a red
    // trace two pixels thick that starts four pixels under the gridline and falls gently to the right.
    private const int GridY = 150;
    private static double RedY(double x) => GridY + 4 + (x - 100) * 0.05;

    private static RasterImage Picture()
    {
        const int w = 600, h = 400;
        var rgba = Enumerable.Repeat((byte)255, w * h * 4).ToArray();
        void Set(int x, int y, int rgb)
        {
            int i = 4 * (y * w + x);
            (rgba[i], rgba[i + 1], rgba[i + 2]) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        for (int x = 100; x <= 500; x++) { Set(x, 50, 0); Set(x, 350, 0); Set(x, GridY, 0); }
        for (int y = 50; y <= 350; y++) { Set(100, y, 0); Set(500, y, 0); }
        for (int x = 101; x < 500; x++)
        {
            int y = (int)Math.Round(RedY(x));
            Set(x, y, 0xD62728);
            Set(x, y + 1, 0xD62728);
        }
        return new RasterImage(w, h, rgba);
    }

    private static ManualPlot Plot() => new(0, ManualQuad.Rectangle(100, 50, 500, 350),
        new ManualAxis(1e9, 2e9, AxisScale.Linear), new ManualAxis(-30, 0, AxisScale.Linear), null, 1e9, true);

    [Fact]
    public void AClickBetweenATraceAndABlackGridline_FollowsTheTrace_AndNamesTheGridline()
    {
        var followed = ManualCurves.Follow(Picture(), Plot(), (150, GridY + 2));
        Assert.True(followed.Ok, followed.Refusal);
        Assert.Equal(0xD62728, followed.Rgb);
        Assert.All(followed.Fixpoints, p => Assert.InRange(Math.Abs(p.Py - RedY(p.Px)), 0, 1.5));
        var other = Assert.NotNull(followed.Other);
        Assert.Equal(0x000000, other.Rgb);
        Assert.InRange(other.DistancePx, 1, 4);
    }

    [Fact]
    public void AFollowedCurveWithIs_WritesAOnePortTouchstone()
    {
        string dir = Agent.Dir("follow");
        string png = Path.Combine(dir, "s11.png");
        File.WriteAllBytes(png, Picture().EncodePng());
        string s1p = Path.Combine(dir, "x.s1p");
        var run = CliProcess.Run(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), [],
            "recognize", png, "--manual-box", "100px,50px,500px,350px", "--x-range", "1GHz..2GHz", "--y-range", "-30..0",
            "--follow", $"S11=150,{GridY + 2},is=S11:db", "-o", s1p, "--json");
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Contains("recognize.plot.followed", Agent.Ids(Agent.Doc(run.StdOut)));
        var net = RfCore.TouchstoneIO.ReadFile(s1p);
        Assert.Equal(1, net.Ports);
        Assert.True(net.Frequencies.Length > 2);
    }
}

/// <summary>R-emb5-12: every type the schematic reference names has its own field table, the tables travel once, and
/// the tuning example is one the reader takes.</summary>
public sealed class SchematicReferenceTests
{
    [Fact]
    public void EveryTypeNamed_HasItsOwnTable()
    {
        var blocks = CircuitRF.Cli.DocumentSchema.Json(CircuitRF.Cli.DocumentSchema.Find("schematic")!);
        var tables = blocks.Select(b => b.Type.Split("   (")[0]).ToHashSet(StringComparer.Ordinal);
        // Lower-case words are JSON shapes (string, bool, integer, number); these three are spellings, not types.
        string[] notTypes = ["JSON", "ARGB", "DateTime"];
        var named = blocks.SelectMany(b => b.Fields)
                          .SelectMany(f => Regex.Matches(f.Type, "[A-Z][A-Za-z0-9]*").Select(m => m.Value))
                          .Where(n => !notTypes.Contains(n))
                          .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(named.Except(tables));
        foreach (string t in new[] { "TunableEntry", "OptimizationGoal", "OptimizerSettings", "StatSpread", "GoalType", "StatDistribution" })
            Assert.Contains(t, tables);
    }

    [Fact]
    public void TheTablesTravelOnce_AndTheTuningExampleReads()
    {
        var text = CliProcess.Run(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), [], "reference", "schematic", "--json");
        var reference = Agent.Doc(text.StdOut)["result"]!["reference"]!;
        Assert.Null(reference["schema"]);
        string page = reference["topic"]!["text"]!.GetValue<string>();
        Assert.Contains("\nTunableEntry\n", page, StringComparison.Ordinal);

        var schema = Agent.Doc(CliProcess.Run(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), [],
                                              "reference", "schematic", "--tables", "schema", "--json").StdOut)["result"]!["reference"]!;
        Assert.NotNull(schema["schema"]);
        Assert.DoesNotContain("\nTunableEntry\n", schema["topic"]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        // The preamble's tuning block, read by the .csch reader's own options.
        int at = page.IndexOf("\"Tuning\": {", StringComparison.Ordinal);
        int open = page.IndexOf('{', at), depth = 0, end = open;
        for (; end < page.Length; end++)
            if (page[end] == '{') depth++;
            else if (page[end] == '}' && --depth == 0) break;
        var tuning = JsonSerializer.Deserialize<CschTuning>(page[open..(end + 1)], SchematicPersistence.ReaderOptions)!.ToSetup();
        Assert.Equal("0603 C0G 50V", tuning.Variables[0].Parts);
        Assert.Equal(StatDistribution.Gauss, tuning.Variables[1].Distribution);
        Assert.Equal("3", tuning.Variables[1].Spread!.Sigmas);
        Assert.Equal(GoalType.Le, tuning.Goals[0].Type);
    }
}

/// <summary>R-emb5-13 (and R-emb5-8's import library summary): a vendor part placed by a .cnl line elaborates as the
/// schematic placing it does, and its Part key is a tunable check accepts.</summary>
public sealed class NetlistVendorPartTests : IDisposable
{
    private readonly string _dir = Agent.Dir("vendorcnl");
    private readonly string _lib;
    private readonly string _ws;
    private readonly string _schDir;
    private const string Part = "TSTC0402A0001";

    public NetlistVendorPartTests()
    {
        _lib = Path.Combine(_dir, "lib");
        VendorLibraryFixture.Write(_lib);
        _ws = WorkspaceCreate.Create(_dir, "ws", "pcb-2layer_FR-4_70mil_1oz", vendorLibraries: [("Parts", _lib)]).WorkspaceDir;
        _schDir = Path.Combine(_ws, "cells", "top", "schematic");
        Directory.CreateDirectory(_schDir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string FileRef => VendorLibraryRefs.RefFor("Parts", "capacitors/series-a.lib");

    /// <summary>The part's own elaborated components, by path — what the netlist runs.</summary>
    private static List<string> PartComponents(Library lib, TestBench tb)
    {
        using var nl = new Elaborator(lib).Elaborate(tb);
        return [.. nl.Components.Where(c => c.InstancePath == "C1" || c.InstancePath.StartsWith("C1.", StringComparison.Ordinal))
                                .Select(c => c.InstancePath + " " + c.ComponentType + " "
                                           + string.Join(",", c.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                                                                          .Select(p => $"{p.Key}={p.Value}")))
                                .OrderBy(s => s, StringComparer.Ordinal)];
    }

    [Fact]
    public void ACnlLine_ElaboratesAsTheSchematicPlacingThePart()
    {
        // The schematic: the part's own component, as the palette places it.
        var comp = new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.SpiceModel, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        comp.Parameters.First(p => p.Name == "File").Expression = FileRef;
        comp.Parameters.First(p => p.Name == "Name").Expression = Part;
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        model.Components.Add(comp);
        var extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
        var (sLib, sTb) = new CnlReader().Read(CnlWriter.Write(extracted.TestBench, extracted.Library, "emb5"), "tb", _schDir);
        var fromSchematic = PartComponents(sLib, sTb);
        Assert.NotEmpty(fromSchematic);

        string cnl = Path.Combine(_schDir, "part.cnl");
        File.WriteAllText(cnl, $"SpiceModel:C1 a b File=\"{FileRef}\" Name={Part}\nR:R1 a 0 R=50 Ohm\nR:R2 b 0 R=50 Ohm\n");
        var (nLib, nTb) = CnlTechnologyBinding.ReadFile(cnl, out var parts);
        Assert.Equal(fromSchematic, PartComponents(nLib, nTb));
        Assert.Equal("C1.Part", Assert.Single(parts).Key);
    }

    [Fact]
    public void APartSearchOnTheCnl_PassesCheck_AndImportLibraryHasASummary()
    {
        string cnl = Path.Combine(_schDir, "search.cnl");
        File.WriteAllText(cnl, $"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            SpiceModel:C1 a b File="{FileRef}" Name={Part}
            Port:P2 b 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
            tune C1.Part parts="C0G" opt=1
            goal Pass = mag(SP1.S(2,1)) analysis=SP1 ge 0.5
            optimize algorithm=auto
            """);
        var check = OptCli.Run("check", cnl, "--json");
        var ids = Agent.Ids(Agent.Doc(check.StdOut)).ToList();
        Assert.True(check.Exit == 0, check.StdOut);
        Assert.DoesNotContain(ids, id => id.Contains("unresolved", StringComparison.Ordinal) || id.Contains("not-offered", StringComparison.Ordinal));

        var import = OptCli.Run("import", "library", _lib, "--into", _ws, "--name", "Again", "--json", "--result", "summary");
        Assert.True(import.Exit == 0, import.StdErr);
        var doc = Agent.Doc(import.StdOut);
        Assert.Equal(VendorLibraryFixture.Parts, doc["result"]!["library"]!["parts"]!.GetValue<int>());
        Assert.NotNull(doc["result"]!["summarized"]);
    }
}

/// <summary>R-emb5-14: a .cdd written by plot --write-cdd into a subfolder of results/ opens in the Data Display with
/// its traces, resolved as the application resolves it.</summary>
public sealed class HeadlessCddTests
{
    [Fact]
    public async Task ACddWrittenIntoResultsSubfolder_OpensWithItsTraces()
    {
        string root = Agent.Dir("cdd");
        string ws = WorkspaceCreate.Create(root, "ws", null).WorkspaceDir;
        string results = Path.Combine(ws, "results");
        string agent = Path.Combine(results, "agent");
        Directory.CreateDirectory(agent);
        string s2p = Path.Combine(agent, "pad.s2p");
        string cnl = OptCli.Write(agent, "pad.cnl", """
            Port:P1 in  0 Num=1 Z=50 Ohm
            R:R1 in out R=10 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz
            """);
        Assert.Equal(0, OptCli.Run("sparam", cnl, "-o", s2p).Exit);
        string cdd = Path.Combine(agent, "pad.cdd");
        var plot = OptCli.Run("plot", s2p, "-o", Path.Combine(agent, "pad.svg"), "--write-cdd", cdd, "--trace", "cube=S,i=2,j=1,y=db20");
        Assert.True(plot.Exit == 0, plot.StdErr);

        // As the application opens it: the workspace's results/ as the results root.
        var vm = new DataDisplayDocumentViewModel();
        vm.Window.DataSourceLibrary.ResultsRootProvider = () => results;
        await vm.Window.LoadAllAsync(cdd);
        var trace = vm.Window.DataDisplay!.Plots.First().Inspector.Traces[0].Trace;
        Assert.Equal(11, trace.Points.Count);

        // The Touchstone it names reaches the source list with no edit of the workspace's known files.
        vm.Window.RefreshAvailableDataSources();
        Assert.Contains(vm.Window.DataSourceLibrary.AvailableDataSources, d => string.Equals(d.AbsolutePath, s2p, StringComparison.OrdinalIgnoreCase));
    }
}
