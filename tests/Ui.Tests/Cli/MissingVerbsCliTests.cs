// ================================================================
//  MissingVerbsCliTests.cs — AUT-11's gates
//  (docs/sonnet-briefs/brief-automation-11-missing-verbs.md §Gates).
//
//  Four capabilities a client reached for and did not find: `netlist`, `plot`, `find`, and a
//  `create` that makes its own parent. Each is gated the way AuthoringCliVerbTests and
//  RenderCliVerbTests already gate a verb — the REAL CLI as a separate process, compared BYTE FOR
//  BYTE against the in-process call it claims to be forwarding to. Asserting exit code 0 and a
//  non-empty file would pass just as happily if the verb had grown its own copy of the extraction
//  or of the plot composer, which is the one failure these verbs exist to avoid.
//
//  The comment-stripped source scans are the other half of that claim and cannot be checked by
//  running anything: a byte-identical result proves the two paths AGREE today, not that they are
//  the same code. Comments are stripped first — this file's own prose names every symbol involved,
//  and so does the verb's (project-brief-harmonicarf-h8 records that trap).
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Render.DataDisplay;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class MissingVerbsCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-aut11-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>A real shipped schematic, not a fixture built here: the claim is about what the
    /// application extracts, and a model assembled in a test is one this test also decided the shape
    /// of.</summary>
    private static string Schematic => Path.Combine(
        RepoRoot(), "src", "Ui", "resources", "doc-schematics", "Example_SParam_LC.csch");

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ══ R-aut11-1: the extraction ═══════════════════════════════════════════

    /// <summary>
    /// Gate 1a. The `.cnl` the VERB writes, as a process, is byte for byte the text
    /// <see cref="CircuitSource.CnlTextOf(string)"/> returns in process — which is
    /// <c>NetExtractor.Extract</c> followed by <c>CnlWriter.Write</c>, the first half of the round
    /// trip the GUI's own Simulate performs.
    /// </summary>
    [Fact]
    public void Netlist_WritesTheSameBytesAsTheExtractionTheGuiPerforms()
    {
        string outPath = Path.Combine(Dir("extract"), "lc.cnl");
        var run = RunCli("netlist", Schematic, "-o", outPath);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);

        Assert.Equal(CircuitSource.CnlTextOf(Schematic), File.ReadAllText(outPath));
    }

    /// <summary>
    /// Gate 1b, and the property that makes this verb the REFERENCE ANSWER rather than merely a
    /// convenience: the file it hands over is the same bytes a run consumes. Both sides go through
    /// one function, so a second provenance line or a second ordering here would be a netlist that
    /// runs differently from the schematic it came out of, with nothing saying so.
    /// </summary>
    [Fact]
    public void TheNetlistWritten_IsTheOneARunConsumes()
    {
        string outPath = Path.Combine(Dir("same"), "lc.cnl");
        Assert.Equal(0, RunCli("netlist", Schematic, "-o", outPath).ExitCode);

        // What a run verb reads for the same schematic, re-emitted from the same extraction.
        Assert.Equal(CircuitSource.CnlTextOf(Schematic), File.ReadAllText(outPath));

        // And it is a netlist the reader accepts as itself.
        var check = RunCli("check", outPath);
        Assert.True(check.ExitCode == 0, check.StdErr + check.StdOut);
    }

    /// <summary>
    /// Gate 1c: extract a netlist from a schematic and run it headlessly. The run of the EXTRACTED
    /// file and the run of the SCHEMATIC produce identical results — which is the whole claim of
    /// letting a run verb take a `.csch`.
    /// </summary>
    [Fact]
    public void ARunOfTheSchematic_AndARunOfItsExtraction_AgreeByteForByte()
    {
        string dir = Dir("run");
        string cnl = Path.Combine(dir, "lc.cnl");
        Assert.Equal(0, RunCli("netlist", Schematic, "-o", cnl).ExitCode);

        string fromCnl  = Path.Combine(dir, "from-cnl.s2p");
        string fromCsch = Path.Combine(dir, "from-csch.s2p");

        var a = RunCli("sparam", cnl, "-o", fromCnl);
        Assert.True(a.ExitCode == 0, a.StdErr + a.StdOut);
        var b = RunCli("sparam", Schematic, "-o", fromCsch);
        Assert.True(b.ExitCode == 0, b.StdErr + b.StdOut);

        // Every line but the provenance write-timestamp the Touchstone carries by design — the same
        // exclusion EmCliVerbTests makes, and for the same reason.
        Assert.Equal(WithoutTimestamps(File.ReadAllLines(fromCnl)),
                     WithoutTimestamps(File.ReadAllLines(fromCsch)));
    }

    /// <summary>
    /// A run verb's <c>-o</c> makes the folder it was told to write into.
    ///
    /// <para><b>Because the GUI's own writer does.</b> <c>ResultsWriter.WriteRun</c> creates
    /// <c>&lt;workspace&gt;/results</c> on its way past, which is why Simulate works on a workspace
    /// that has never been run. Headless, <c>-o results/Cell.npy</c> on that same workspace failed
    /// with <i>"could not find a part of the path"</i> — so the documented way to reproduce a run
    /// was the one way that required somebody to have run it already. It went unnoticed for as long
    /// as every example shipped its results folder.</para>
    ///
    /// <para>Every verb, not one: they wrote through four different writers, and `netlist`,
    /// `render` and `plot` had each solved it separately.</para>
    /// </summary>
    [Theory]
    [InlineData("sparam", "out.s2p")]
    [InlineData("sparam", "out.npy")]
    [InlineData("hb",     "out.npy")]
    public void ARunVerbCreatesTheOutputFolderItWasGiven(string verb, string name)
    {
        string cnl = Path.Combine(Dir("mkdir"), "bench.cnl");
        File.WriteAllText(cnl, """
            Port:P1 a 0 Num=1 Z=50 Ohm
            R:R1 a b R=100 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=3
            """);

        // Two levels deep, so the fix cannot be a single mkdir of the immediate parent.
        string outPath = Path.Combine(_root, "mkdir", "never", "made", name);
        Assert.False(Directory.Exists(Path.GetDirectoryName(outPath)!));

        var run = RunCli(verb, cnl, "-o", outPath);
        output.WriteLine(run.StdErr);

        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.True(File.Exists(outPath), $"{verb} reported success and wrote no {outPath}.");
    }

    /// <summary>
    /// The whole of R-aut11-1's motive, as a refusal. `run` on a document that is neither used to
    /// hand the JSON to <c>CnlReader</c> and report its first key as a missing cell name — a message
    /// that sent a caller looking for a library that was never involved.
    /// </summary>
    [Theory]
    [InlineData("sparam")]
    [InlineData("dc")]
    [InlineData("hb")]
    [InlineData("lp")]
    public void ARunOfAWrongDocumentKind_IsRefusedByKind(string verb)
    {
        string tech = Path.Combine(Dir("kind"), "x.ctech");
        File.WriteAllText(tech, "{}");

        var run = RunCli(verb, tech, "--json");
        Assert.NotEqual(0, run.ExitCode);

        var ids = Ids(run.StdOut);
        Assert.Contains("cli.input.wrong-kind", ids);
        Assert.DoesNotContain(ids, id => id.Contains("elaborat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("technology", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>Every run verb reaches its circuit through the ONE reader that knows about both
    /// kinds. A verb that called <c>CnlReader.ReadFile</c> directly would take a `.cnl` and hand a
    /// `.csch` to the netlist parser again, which is the defect this landed to remove.</summary>
    [Fact]
    public void NoRunVerb_KeepsItsOwnNetlistRead()
    {
        string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "CliEntry.cs")));

        Assert.DoesNotContain("CnlReader.ReadFile(", code, StringComparison.Ordinal);
        // Five call sites: sparam, dc, hb, the shared lp/lpp body, and elab.
        Assert.Equal(5, Regex.Matches(code, Regex.Escape("CircuitSource.ReadRunInput(")).Count);
    }

    /// <summary>
    /// R-aut11-1's "no second copy of the extraction". Across the whole CLI, exactly one file turns
    /// a schematic into netlist TEXT, and it is the one the verb and every run verb go through.
    ///
    /// <para>The scan is on <c>CnlWriter.Write</c> rather than on <c>NetExtractor.Extract</c>
    /// because those are two different claims. <c>check</c> legitimately calls the extractor on its
    /// own account — extraction is where a naming conflict between two labels on one physical net is
    /// reported, and nothing else in the tree reports it — and then goes through
    /// <see cref="CircuitSource"/> for the netlist half like everyone else. What must not exist
    /// twice is the WRITE, because that is what decides the bytes.</para>
    /// </summary>
    [Fact]
    public void TheExtractionToNetlistText_ExistsInExactlyOnePlaceInTheCli()
    {
        var naming = new List<string>();
        foreach (string file in Directory.GetFiles(
                     Path.Combine(RepoRoot(), "src", "Cli"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal)) continue;
            if (StripComments(File.ReadAllText(file)).Contains("CnlWriter.Write(", StringComparison.Ordinal))
                naming.Add(Path.GetFileName(file));
        }

        Assert.Equal(["CircuitSource.cs"], naming);
    }

    [Fact]
    public void Netlist_RefusesAnOutputThatIsNotACnl()
    {
        var run = RunCli("netlist", Schematic, "-o", Path.Combine(Dir("badout"), "x.svg"), "--json");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("netlist.output.not-cnl", Ids(run.StdOut));
    }

    /// <summary>Without <c>-o</c> the netlist is the result: verbatim on stdout, and in the
    /// document. `circuitrf netlist x.csch &gt; x.cnl` must be the file and not the file with a
    /// newline added to it.</summary>
    [Fact]
    public void Netlist_WithNoOutput_PutsTheTextOnStdoutAndInTheDocument()
    {
        var plain = RunCli("netlist", Schematic);
        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(CircuitSource.CnlTextOf(Schematic), plain.StdOut);

        var json = RunCli("netlist", Schematic, "--json");
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(CircuitSource.CnlTextOf(Schematic),
                     JsonNode.Parse(json.StdOut)!["result"]!["document"]!["text"]!.GetValue<string>());
    }

    /// <summary>A cell folder and a workspace resolve as <c>render</c> resolves them — the same
    /// <c>CellLookup</c> and <c>CellFolder.ResolvePrimary</c>, so the cell extracted is the cell that
    /// verb would have drawn.</summary>
    [Fact]
    public void Netlist_TakesACellFolderAndAWorkspaceWithCell()
    {
        string ws = MakeWorkspaceWithCell("cells", "LC");

        string viaCell = Path.Combine(_root, "via-cell.cnl");
        string viaWs   = Path.Combine(_root, "via-ws.cnl");

        Assert.Equal(0, RunCli("netlist", Path.Combine(ws, "LC"), "-o", viaCell).ExitCode);
        Assert.Equal(0, RunCli("netlist", ws, "--cell", "LC", "-o", viaWs).ExitCode);

        Assert.Equal(File.ReadAllText(viaCell), File.ReadAllText(viaWs));

        // A workspace with no --cell is the dialog's own question, so it is a refusal naming the
        // flag rather than a guess at which cell was meant.
        var refused = RunCli("netlist", ws, "--json");
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("netlist.workspace.cell-required", Ids(refused.StdOut));
    }

    // ══ R-aut11-2: one plotting path ════════════════════════════════════════

    /// <summary>
    /// The gate the brief asks for: a plot rendered by the new tool is BYTE-IDENTICAL to one
    /// rendered from the equivalent hand-authored data display. <c>--write-cdd</c> is what makes it
    /// checkable — the document `plot` built is handed to `render`, and the two pictures are
    /// compared as files.
    /// </summary>
    [Theory]
    [InlineData("svg")]
    [InlineData("pdf")]
    [InlineData("png")]
    public void APlot_IsTheSameBytesAsRenderingTheDisplayItBuilt(string format)
    {
        string dir = Dir("plot-" + format);
        string s2p = Sparam(dir);

        // The SAME file name in two directories, not two names in one: a PDF's metadata Title is
        // taken from the output path, exactly as the application's own Export takes it, so `a.pdf`
        // and `b.pdf` would differ in that one field and nowhere else — the sort of difference that
        // gets excluded from a gate rather than understood.
        string viaPlot   = Path.Combine(Dir("plot-" + format + "-a"), "one." + format);
        string viaRender = Path.Combine(Dir("plot-" + format + "-b"), "one." + format);
        string cdd       = Path.Combine(dir, "one.cdd");

        var plot = RunCli("plot", s2p, "-o", viaPlot, "--write-cdd", cdd,
                          "--trace", "cube=S,i=1,j=1,y=db",
                          "--trace", "cube=S,i=2,j=1,y=db",
                          "--title", "LC lowpass");
        Assert.True(plot.ExitCode == 0, plot.StdErr + plot.StdOut);

        var render = RunCli("render", cdd, "--data", s2p, "-o", viaRender);
        Assert.True(render.ExitCode == 0, render.StdErr + render.StdOut);

        AssertSameBytes(viaPlot, viaRender);
    }

    /// <summary>
    /// The 1-based/0-based trap, pinned. On an <c>i</c>/<c>j</c> axis the shorthand's integer token
    /// is a PORT NUMBER, so <c>i=2,j=1</c> must land on slice indices 1 and 0. Off by one here is the
    /// quietest possible wrong answer: S12 and S21 are both legal curves, and on a reciprocal part
    /// they are the same curve.
    /// </summary>
    [Fact]
    public void PortNumbers_LandOnTheMatrixEntryTheyName()
    {
        string dir = Dir("ports");
        string cdd = Path.Combine(dir, "one.cdd");

        Assert.Equal(0, RunCli("plot", Sparam(dir), "-o", Path.Combine(dir, "p.svg"),
                               "--write-cdd", cdd, "--trace", "cube=S,i=2,j=1,y=db").ExitCode);

        var config = JsonSerializer.Deserialize<DataDisplayConfig>(
            File.ReadAllText(cdd), DataDisplayJson.Options)!;
        var trace = config.Tabs[0].Plots[0].Traces[0];

        Assert.Equal("S", trace.CubeName);
        Assert.Equal(CubeTransform.dB, trace.CubeTransform);
        Assert.Equal(
            [("freq", AxisRole.KeepAsX, 0), ("i", AxisRole.PinToIndex, 1), ("j", AxisRole.PinToIndex, 0)],
            trace.CubeSlice.Select(s => (s.AxisName, s.Role, s.Index)).ToArray());
    }

    /// <summary>An empty plot is a valid picture that exports cleanly and looks exactly like a
    /// measurement that came back empty (R-rnd4-4). A plot with no trace is that picture, and it is
    /// refused rather than drawn.</summary>
    [Fact]
    public void APlotWithNoTrace_IsRefusedRatherThanDrawnEmpty()
    {
        string dir = Dir("empty");
        string outPath = Path.Combine(dir, "p.svg");

        var run = RunCli("plot", Sparam(dir), "-o", outPath, "--json");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("plot.args.trace-required", Ids(run.StdOut));
        Assert.False(File.Exists(outPath));
    }

    /// <summary>A cube the result does not hold, and a port the cube does not have, are refusals
    /// that NAME what is there — not a picture with nothing in it.</summary>
    [Fact]
    public void AnUnresolvableTrace_IsRefusedNamingWhatIsThere()
    {
        string dir = Dir("nocube");
        string s2p = Sparam(dir);

        // A cube the file does not hold is refused by NAME, listing what it does hold — in every
        // spelling, including the bare one the parser answers with "Missing '['".
        foreach (string typed in new[] { "cube=Pout,y=db", "cube=Pout,i=1,j=1", "cube=mag(Pout)" })
        {
            var noCube = RunCli("plot", s2p, "-o", Path.Combine(dir, "a.svg"), "--trace", typed, "--json");
            Assert.True(noCube.ExitCode != 0, typed);
            Assert.Contains("plot.trace.no-such-cube", Ids(noCube.StdOut));
            Assert.Contains("It holds:", noCube.StdOut, StringComparison.Ordinal);
        }

        var noPort = RunCli("plot", s2p, "-o", Path.Combine(dir, "b.svg"),
                            "--trace", "cube=S,i=9,j=1,y=db", "--json");
        Assert.NotEqual(0, noPort.ExitCode);
        Assert.Contains("plot.trace.unresolved", Ids(noPort.StdOut));

        var both = RunCli("plot", s2p, "-o", Path.Combine(dir, "c.svg"),
                          "--trace", "cube=S[:,2,1],i=2,j=1", "--json");
        Assert.NotEqual(0, both.ExitCode);
        Assert.Contains("plot.trace.ports-with-slice", Ids(both.StdOut));
    }

    /// <summary>R-aut11-2's "one plotting path, not two", checked as source rather than as
    /// agreement: the verb composes nothing and encodes nothing of its own.</summary>
    [Fact]
    public void ThePlotVerb_ComposesNothingOfItsOwn()
    {
        string code = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Cli", "PlotVerb.cs")));

        foreach (string owned in new[] { "PlotComposer", "PlotDocumentWriter", "SKCanvas", "PagePlacement" })
            Assert.DoesNotContain(owned, code, StringComparison.Ordinal);

        Assert.Contains("RenderDataDisplay.Draw(", code, StringComparison.Ordinal);
        Assert.Contains("CubeTraceSpecParser.TryParse(", code, StringComparison.Ordinal);
    }

    // ══ R-aut11-3: find ═════════════════════════════════════════════════════

    [Fact]
    public void Find_ListsTheWorkspacesTheirCellsViewsAndAnalyses()
    {
        string ws = MakeWorkspaceWithCell("find", "LC");

        var run = RunCli("find", Path.GetDirectoryName(ws)!, "--json");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);

        var find = JsonNode.Parse(run.StdOut)!["result"]!["find"]!;
        var workspaces = find["workspaces"]!.AsArray();
        Assert.Single(workspaces);

        var cells = workspaces[0]!["cells"]!.AsArray();
        var cell  = Assert.Single(cells)!;
        Assert.Equal("LC", cell["name"]!.GetValue<string>());

        Assert.Contains(cell["views"]!.AsArray(),
                        v => v!["type"]!.GetValue<string>() == "schematic");
        Assert.Contains(cell["analyses"]!.AsArray(), a => a!.GetValue<string>() == "SP1");
    }

    /// <summary>
    /// The walk is bounded, and it SAYS so. A listing that quietly stopped short is the one failure
    /// this verb must not have: a caller reads a short answer as "the workspace is not here".
    /// </summary>
    [Fact]
    public void Find_IsBounded_AndSaysWhenItStoppedShort()
    {
        string root = Dir("bounded");
        string deep = Path.Combine(root, "a", "b", "c", "d", "Amp");
        Assert.Equal(0, RunCli("new", "workspace", deep, "--tech", "none").ExitCode);

        var shallow = RunCli("find", root, "--depth", "2", "--json");
        Assert.Equal(0, shallow.ExitCode);
        var shallowFind = JsonNode.Parse(shallow.StdOut)!["result"]!["find"]!;
        Assert.Empty(shallowFind["workspaces"]!.AsArray());
        Assert.True(shallowFind["truncated"]!.GetValue<bool>());
        Assert.Contains("find.walk.truncated", Ids(shallow.StdOut));

        var deeper = RunCli("find", root, "--depth", "6", "--json");
        Assert.Equal(0, deeper.ExitCode);
        var deeperFind = JsonNode.Parse(deeper.StdOut)!["result"]!["find"]!;
        Assert.Single(deeperFind["workspaces"]!.AsArray());
        Assert.False(deeperFind["truncated"]!.GetValue<bool>());
    }

    /// <summary>
    /// Nothing outside the root ever appears in the listing. The one way a bounded walk stops being
    /// bounded — and a confined one stops being confined — is a directory symbolic link, so one is
    /// planted and the listing is checked against it.
    /// </summary>
    [Fact]
    public void Find_NeverLeavesTheRoot()
    {
        string outside = Dir("outside");
        Assert.Equal(0, RunCli("new", "workspace", Path.Combine(outside, "Elsewhere"), "--tech", "none").ExitCode);

        string root = Dir("inside");
        Assert.Equal(0, RunCli("new", "workspace", Path.Combine(root, "Here"), "--tech", "none").ExitCode);

        try { Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside); }
        catch (Exception ex)
        {
            // Windows needs Developer Mode or an elevated process to make one. The assertion below
            // is about what a link does, so with no link there is nothing to assert — reported
            // rather than passed silently.
            output.WriteLine($"no symbolic link on this machine, half of this gate did not run: {ex.Message}");
            return;
        }

        var run = RunCli("find", root, "--depth", "6", "--json");
        Assert.Equal(0, run.ExitCode);

        Assert.DoesNotContain("Elsewhere", run.StdOut, StringComparison.Ordinal);
        var names = JsonNode.Parse(run.StdOut)!["result"]!["find"]!["workspaces"]!.AsArray()
                    .Select(w => w!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["Here"], names);
    }

    [Fact]
    public void Find_OnAnEmptyFolder_SaysSoRatherThanReturningNothing()
    {
        var run = RunCli("find", Dir("bare"), "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Empty(JsonNode.Parse(run.StdOut)!["result"]!["find"]!["workspaces"]!.AsArray());
        Assert.Contains("find.nothing", Ids(run.StdOut));
    }

    // ══ R-aut11-4: create makes its parent ══════════════════════════════════

    /// <summary>
    /// The refusal this replaces was defensible and was discovered by hitting it: `create` under a
    /// path whose parent does not exist failed with "No such directory", and a client with no file
    /// tools of its own had nowhere to go from there.
    /// </summary>
    [Fact]
    public void CreatingAWorkspace_MakesMissingParents_AndSaysSo()
    {
        string ws = Path.Combine(Dir("parents"), "one", "two", "three", "Amp");

        var run = RunCli("new", "workspace", ws, "--tech", "none", "--json");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);

        Assert.True(File.Exists(Path.Combine(ws, ".cws")));
        Assert.Contains("new.parent.created", Ids(run.StdOut));
    }

    /// <summary>An existing workspace is still never overwritten — the parent rule loosened what a
    /// missing DIRECTORY means, and nothing else.</summary>
    [Fact]
    public void CreatingAWorkspaceThatExists_IsStillRefused()
    {
        string ws = Path.Combine(Dir("twice"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws, "--tech", "none").ExitCode);

        var again = RunCli("new", "workspace", ws, "--tech", "none", "--json");
        Assert.NotEqual(0, again.ExitCode);
    }

    /// <summary>R-aut11-4's other half: the server's own instructions say plainly that the client
    /// supplies its own file writing. It was true before and was discovered by hitting it.</summary>
    [Fact]
    public void TheServerInstructions_SayTheClientWritesItsOwnFiles()
    {
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "Serve", "McpServer.cs"));
        Assert.Contains("NO TOOL HERE WRITES A FILE OF YOUR TEXT", code, StringComparison.Ordinal);
        Assert.Contains("does make missing parent directories", code, StringComparison.Ordinal);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A workspace holding one cell whose schematic view is the shipped example.</summary>
    private string MakeWorkspaceWithCell(string where, string cellName)
    {
        string ws = Path.Combine(Dir(where), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws, "--tech", "none").ExitCode);
        Assert.Equal(0, RunCli("new", "cell", ws, cellName, "--views", "schematic").ExitCode);

        // The cell's own empty-but-valid schematic is replaced by the example's, under the name
        // `new cell` already recorded as primary. The other two view sub-folders exist and are
        // empty, which is why the schematic one is named rather than found as the only one.
        string schDir = Path.Combine(ws, cellName, "schematic");
        string file   = Directory.GetFiles(schDir, "*.csch").Single();
        File.Copy(Schematic, file, overwrite: true);
        return ws;
    }

    /// <summary>A real S-parameter result to plot, produced by the verb that produces one.</summary>
    private string Sparam(string dir)
    {
        string s2p = Path.Combine(dir, "lc.s2p");
        var run = RunCli("sparam", Schematic, "-o", s2p);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        return s2p;
    }

    private static string[] WithoutTimestamps(string[] lines)
        => [.. lines.Where(l => !l.Contains("written", StringComparison.OrdinalIgnoreCase))];

    private static void AssertSameBytes(string a, string b)
    {
        var left  = File.ReadAllBytes(a);
        var right = File.ReadAllBytes(b);
        Assert.True(left.Length == right.Length,
                    $"{Path.GetFileName(a)} is {left.Length} bytes, {Path.GetFileName(b)} is {right.Length}");
        Assert.True(left.AsSpan().SequenceEqual(right), $"{a} and {b} differ");
    }

    /// <summary>The diagnostic ids in a <c>--json</c> document, which are the contract; the messages
    /// are not (cli.md §3.2).</summary>
    private static string[] Ids(string document)
    {
        try
        {
            return [.. (JsonNode.Parse(document)?["diagnostics"]?.AsArray() ?? [])
                       .Select(d => d?["id"]?.GetValue<string>() ?? "")];
        }
        catch { return []; }
    }

    /// <summary>Comments stripped, so a scan cannot pass on prose that names the symbol it is
    /// looking for — this file's own text names every one of them.</summary>
    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", "");
    }

    // ── driving the verb ─────────────────────────────────────────────────────

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        // Both pipes drained CONCURRENTLY: reading one to the end and only then the other deadlocks
        // the moment the child fills the pipe it is not being read from.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(MissingVerbsCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }
}
