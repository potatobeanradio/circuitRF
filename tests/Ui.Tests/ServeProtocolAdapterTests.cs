// ================================================================
//  ServeProtocolAdapterTests.cs — the gate for `circuitrf serve`
//  (brief-automation-5-protocol-adapter.md §5).
//
//  ── What these tests are actually pinning ─────────────────────────────────────────────────────
//
//  §5.1 is the one this brief exists to pass. R-aut-13 says nothing may be reachable through the
//  protocol server that is not reachable through the CLI, and vice versa — so for every tool, the
//  document that comes back through the server is compared BYTE FOR BYTE against the document
//  `circuitrf <verb> --json` writes for the same work. A tool that can do something the CLI cannot,
//  or vice versa, fails here, which is the point.
//
//  Two things are legitimately variable and are normalized, in the spirit of EmCliVerbTests' single
//  exempted provenance line:
//    * the adapter RESOLVES paths (that is R-aut5-8's confinement), so the CLI side is given the
//      resolved path rather than the relative one — the same path, spelled the way the server had
//      to spell it;
//    * a verb that CREATES something cannot create it twice (AUT-3 R-aut3-6 refuses an overwrite),
//      so those two calls are given different destinations and the destination is substituted out.
//  Nothing else is exempted. In particular the diagnostics, their ids and their arguments, the
//  outputs list and the whole result payload are compared verbatim.
//
//  §5.2 is the stdout audit. The framing is newline-delimited JSON-RPC, so "not one byte outside
//  the framing" is exactly "every line of stdout parses as a JSON-RPC message" — a stray write with
//  a newline is its own unparseable line, and one without a newline corrupts the frame it prefixes.
//  It is run over every tool the server exposes INCLUDING a real EM run and a run backed by a real
//  device-worker child process, which are the two loudest things this program does.
//
//  §5.3 and §5.4 are the confinement and the lifecycle.
//
//  Every process launch is the already-built CircuitRF.Cli.dll rather than `dotnet run --project`,
//  which is a hang inside `dotnet test` and not merely a cost (EmCliVerbTests.RunCli records why),
//  and both of a child's pipes are drained concurrently — `serve` says enough on stderr during an EM
//  run to fill that pipe's buffer and deadlock a sequential reader.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented names.
// ================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Reference;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Layout;
using CircuitRF.Cli.Serve;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ServeProtocolAdapterTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-serve-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The server root for every fixture here. Created eagerly: `serve` refuses a root that
    /// is not a directory, which is the right refusal and a confusing way for a test to fail.</summary>
    private string Root { get { Directory.CreateDirectory(_root); return _root; } }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ══ §5.1 — parity, one call per tool ═════════════════════════════════════════════════════════

    /// <summary><c>run</c>. The result document of a real analysis, cubes and all.</summary>
    [Fact]
    public void Run_ThroughTheServer_IsTheDocumentTheCliWrites()
    {
        // Copied under the root, because the server refuses a path outside it — which is the point
        // of the root, and applies to this test's own fixtures as much as to a client's.
        string cnl = Path.Combine(CopyTestData("Hero1"), "hero1.cnl");
        string snp = Path.Combine(Dir("out"), "hero1.s4p");

        using var server = Start(Root);
        string mine = server.Call("run", new JsonObject
        {
            ["analysis"] = "sparam", ["path"] = cnl, ["output"] = snp,
        });

        AssertSameDocument(mine, Cli("sparam", cnl, "-o", snp, "--json"));
    }

    /// <summary><c>run</c> again, on the path that starts a second process of its own. The device
    /// worker is what makes this worth a second parity case: it is the loudest capability here, and
    /// it is the one whose output crosses a pipe before it reaches either adapter.</summary>
    [Fact]
    public void Run_WithADeviceWorker_IsTheDocumentTheCliWrites()
    {
        string kits = DeviceWorkerKit();
        string cnl  = WorkerNetlist();

        using var server = Start(_root, kits);
        string mine = server.Call("run", new JsonObject { ["analysis"] = "dc", ["path"] = cnl });

        AssertSameDocument(mine, Cli("dc", cnl, "--kits", kits, "--json"));
    }

    /// <summary><c>check</c>.</summary>
    [Fact]
    public void Check_ThroughTheServer_IsTheDocumentTheCliWrites()
    {
        string workspace = AuthorWorkspace("Amp");

        using var server = Start(Root);
        string mine = server.Call("check", new JsonObject { ["path"] = workspace });

        AssertSameDocument(mine, Cli("check", workspace, "--json"));
    }

    /// <summary><c>explain</c>, with all three of its questions off — the walk alone.</summary>
    [Fact]
    public void Explain_ThroughTheServer_IsTheDocumentTheCliWrites()
    {
        string workspace = AuthorWorkspace("Amp");
        string csch      = Path.Combine(workspace, "Stage1", "schematic", "Stage1.csch");

        using var server = Start(Root);
        string mine = server.Call("explain", new JsonObject { ["path"] = csch });

        AssertSameDocument(mine, Cli("explain", csch, "--json"));
    }

    /// <summary>
    /// <c>explain</c>'s <c>analysis</c> argument, and the one thing about it a type of "string"
    /// cannot say: <b>the empty string is the BARE flag, not a name.</b>
    ///
    /// <para><c>explain --analysis</c> takes its name as an optional following token, so an empty
    /// value forwarded as a value asked about a chain called <c>""</c> and was refused —
    /// <c>No analysis named ''</c> — while the argument's own description said it asked about all of
    /// them. The description was the true half; the translation is what changed
    /// (<c>OptKind.StrOptional</c>).</para>
    /// </summary>
    [Fact]
    public void ExplainWithAnEmptyAnalysisName_AsksAboutEveryChain_AsItsDescriptionSays()
    {
        string cnl = Path.Combine(CopyTestData("Hero3B"), "hero3B_at_compression.cnl");

        using var server = Start(Root);
        string mine = server.Call("explain", new JsonObject { ["path"] = cnl, ["analysis"] = "" });

        Assert.DoesNotContain("explain.analysis.not-found", Ids(mine));
        AssertSameDocument(mine, Cli("explain", cnl, "--analysis", "--json"));

        // …and a NAME still asks about that one, so the empty string is the only special value.
        Assert.Contains("explain.analysis.not-found",
                        Ids(server.Call("explain", new JsonObject
                        {
                            ["path"] = cnl, ["analysis"] = "NoSuchChain",
                        })));
    }

    /// <summary>
    /// RND-3's three questions, each through both adapters (R-rnd5-1, gate 1). They are OPTIONS on
    /// <c>explain</c> rather than three new tools, so a client that could not pass them would have
    /// three CLI capabilities with no protocol spelling — which is exactly the asymmetry R-aut-13
    /// forbids in the direction that is hardest to notice.
    ///
    /// <para><c>--all</c> and <c>--view</c> travel with them for the same reason: they are the
    /// arguments that ANSWER these three questions' own refusals, and a client handed
    /// "a cell folder holding two views — name one with --view" could not act on it otherwise.</para>
    /// </summary>
    [Theory]
    [InlineData("cells")]
    [InlineData("layers")]
    [InlineData("extents")]
    public void ExplainsThreeQueries_ThroughTheServer_AreTheDocumentsTheCliWrites(string question)
    {
        string clay = LayoutFixture();
        string dir  = Path.GetDirectoryName(clay)!;

        // --cells wants a workspace; --layers and --extents want the document itself.
        string path = question == "cells" ? dir : clay;

        using var server = Start(Root);
        string mine = server.Call("explain", new JsonObject { ["path"] = path, [question] = true });

        AssertSameDocument(mine, Cli("explain", path, "--" + question, "--json"));

        // Vacuity guard: a document with no payload at all would satisfy the comparison above.
        Assert.Contains($"\"{question}\"", mine, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>render</c> — RND-5's one new tool, through both adapters (gate 1). A layout, because it is
    /// the kind that carries a technology, a layer selection and a unit rule, so its document has the
    /// most in it to disagree about.
    ///
    /// <para>Both sides are told to write the SAME file. That is deliberate: the document names its
    /// output, so two destinations would have to be substituted out and the substitution is exactly
    /// where a path bug hides. The second write overwrites the first, which is what a re-render does
    /// anyway.</para>
    /// </summary>
    [Fact]
    public void Render_ThroughTheServer_IsTheDocumentTheCliWrites()
    {
        string clay = LayoutFixture();
        string png  = Path.Combine(Dir("pictures"), "line.png");

        using var server = Start(Root);
        string mine = server.Call("render", new JsonObject { ["path"] = clay, ["output"] = png });

        AssertSameDocument(mine, Cli("render", clay, "-o", png, "--json"));

        var doc = JsonDocument.Parse(mine).RootElement;
        Assert.Equal("ok", doc.GetProperty("status").GetString());
        Assert.Equal("png", doc.GetProperty("outputs")[0].GetProperty("kind").GetString());
        Assert.True(File.Exists(png));
    }

    /// <summary>
    /// The other half of <c>render</c>'s surface: the viewport, the layer selection and the detail
    /// tier, each named the way the CLI names it. One call rather than three, because what is being
    /// pinned is the TRANSLATION and the verb only has to agree with itself once per argument.
    ///
    /// <para>The layout coordinates carry their SI unit (R-rnd0-5) and are passed through verbatim —
    /// a client that wrote <c>500um</c> must get the picture <c>500um</c> means, and the adapter has
    /// no business converting it to anything.</para>
    /// </summary>
    [Fact]
    public void RendersViewportLayersAndDetail_ThroughTheServer_AreTheCliDocument()
    {
        string clay = LayoutFixture();
        string svg  = Path.Combine(Dir("pictures"), "region.svg");

        var arguments = new JsonObject
        {
            ["path"]   = clay,
            ["output"] = svg,
            ["window"] = "0um,0um,500um,300um",
            ["layers"] = new JsonArray("Top Copper"),
            ["detail"] = "screen",
            ["size"]   = "640x480",
        };

        using var server = Start(Root);
        string mine = server.Call("render", arguments);

        AssertSameDocument(mine, Cli("render", clay, "-o", svg, "--window", "0um,0um,500um,300um",
                                     "--layers", "Top Copper", "--detail", "screen", "--size", "640x480",
                                     "--json"));

        var render = JsonDocument.Parse(mine).RootElement.GetProperty("result").GetProperty("render");
        Assert.Equal("window", render.GetProperty("viewport").GetProperty("mode").GetString());
        Assert.Equal("screen", render.GetProperty("detail").GetProperty("mode").GetString());
    }

    // ══ R-rnd5-4 — the picture, back through the protocol ════════════════════════════════════════

    /// <summary>
    /// Gate 3. <b>The bytes the client receives, decoded, ARE the file on disk.</b>
    ///
    /// <para>That is the whole claim of R-rnd5-4's first constraint: the adapter attaches what the
    /// verb wrote and does not draw a second time. A re-render would pass a "looks the same"
    /// assertion and fail this one the moment anything about the second call's viewport, theme or
    /// page differed from the first — which is precisely the drift the render series exists to
    /// prevent, in the one place it would be invisible.</para>
    ///
    /// <para>And it is OPT-IN: the same call without the argument comes back with the document
    /// alone. An image is expensive in a way a JSON document is not, and a client that wanted a path
    /// must not be charged for a picture.</para>
    /// </summary>
    [Fact]
    public void TheAttachedPicture_IsTheFileOnDisk_ByteForByte()
    {
        string clay = LayoutFixture();
        string png  = Path.Combine(Dir("pictures"), "attached.png");

        using var server = Start(Root);

        var without = server.CallRaw("render", new JsonObject { ["path"] = clay, ["output"] = png });
        Assert.Single(without);
        Assert.Equal("text", without[0]!["type"]!.GetValue<string>());

        var with = server.CallRaw("render", new JsonObject
        {
            ["path"] = clay, ["output"] = png, ["attachImage"] = true,
        });

        Assert.Equal(2, with.Count);
        var image = with[1]!.AsObject();
        Assert.Equal("image",     image["type"]!.GetValue<string>());
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());

        Assert.Equal(File.ReadAllBytes(png), Convert.FromBase64String(image["data"]!.GetValue<string>()));

        // The document is untouched by the attachment — it is an ADDITIONAL block, which is what
        // keeps the parity gate above meaning what it means.
        AssertSameDocument(with[0]!["text"]!.GetValue<string>(), Cli("render", clay, "-o", png, "--json"));
    }

    /// <summary>
    /// A PDF is attached as a PDF or not at all (R-rnd5-4's third constraint). It is not an image and
    /// is not transcoded into one to make it attachable — that would be the adapter making a
    /// rendering decision, which is the one thing <c>cli.md</c> §11.1 says it never does.
    /// </summary>
    [Fact]
    public void APdf_IsAttachedAsAPdf_NotRasterizedIntoAnImage()
    {
        string clay = LayoutFixture();
        string pdf  = Path.Combine(Dir("pictures"), "line.pdf");

        using var server = Start(Root);
        var content = server.CallRaw("render", new JsonObject
        {
            ["path"] = clay, ["output"] = pdf, ["attachImage"] = true,
        });

        Assert.Equal(2, content.Count);
        var resource = content[1]!.AsObject();
        Assert.Equal("resource", resource["type"]!.GetValue<string>());

        var inner = resource["resource"]!.AsObject();
        Assert.Equal("application/pdf", inner["mimeType"]!.GetValue<string>());
        Assert.Equal(File.ReadAllBytes(pdf), Convert.FromBase64String(inner["blob"]!.GetValue<string>()));
    }

    /// <summary>
    /// Gate 4. <b>Over the cap it refuses; it never truncates and never drops the attachment in
    /// silence.</b>
    ///
    /// <para>Half a PNG is not a smaller PNG, and a client that asked for a picture and received
    /// nothing with nothing said simply asks again — paying for the render twice and learning nothing
    /// the second time. So the answer is the path (which the document already carries) plus a
    /// sentence naming the size, the cap, and at least one argument that would bring it under.</para>
    ///
    /// <para>The fixture is a real render rather than a stubbed size: a 40,000-vertex contour at
    /// <c>--detail full</c> in a vector format is exactly the case the cap exists for, and it is the
    /// same shape as the measured 23.5 MB board in <c>src/Cli/RESOLVED.md</c>.</para>
    /// </summary>
    [Fact]
    public void APictureOverTheCap_ComesBackAsItsPath_WithSomethingToNarrow()
    {
        string clay = HugeLayoutFixture();
        string svg  = Path.Combine(Dir("pictures"), "huge.svg");

        using var server = Start(Root);
        var content = server.CallRaw("render", new JsonObject
        {
            ["path"] = clay, ["output"] = svg, ["detail"] = "full", ["attachImage"] = true,
        });

        long size = new FileInfo(svg).Length;
        output.WriteLine($"the fixture rendered {size:N0} bytes; the cap is {4L * 1024 * 1024:N0}");
        Assert.True(size > 4L * 1024 * 1024,
                    $"the fixture is only {size:N0} bytes, so this gate measured nothing");

        // The document, then the refusal — and NO image block of any kind.
        Assert.Equal(2, content.Count);
        Assert.DoesNotContain(content, c => c!["type"]!.GetValue<string>() is "image" or "resource");

        string note = content[1]!["text"]!.GetValue<string>();
        Assert.Contains("serve.image.too-large", note, StringComparison.Ordinal);
        Assert.Contains(size.ToString(System.Globalization.CultureInfo.InvariantCulture), note,
                        StringComparison.Ordinal);
        Assert.Contains("--detail screen", note, StringComparison.Ordinal);

        // The picture itself was still written, and the document still names it: the cap is about the
        // ENVELOPE, never about the render.
        var outputs = JsonDocument.Parse(content[0]!["text"]!.GetValue<string>()).RootElement
            .GetProperty("outputs");
        Assert.Equal(svg, outputs[0].GetProperty("path").GetString());
        Assert.True(File.Exists(svg));
    }

    /// <summary>
    /// <c>create</c>, both nouns. The two sides create in DIFFERENT directories because AUT-3's
    /// R-aut3-6 refuses to overwrite an existing workspace — so the destination is substituted out
    /// and everything else, including the copied technology's file name, is compared verbatim.
    /// </summary>
    [Fact]
    public void Create_ThroughTheServer_IsTheDocumentTheCliWrites()
    {
        string mineDir = Dir("mine");
        string cliDir  = Dir("theirs");

        using var server = Start(Root);
        string mine = server.Call("create", new JsonObject
        {
            ["what"] = "workspace", ["path"] = Path.Combine(mineDir, "Amp"),
        });

        AssertSameDocument(mine, Cli("new", "workspace", Path.Combine(cliDir, "Amp"), "--json"),
                           (mineDir, "<DEST>"), (cliDir, "<DEST>"));

        string mineCell = server.Call("create", new JsonObject
        {
            ["what"]  = "cell",
            ["workspace"] = Path.Combine(mineDir, "Amp"),
            ["name"]  = "Stage1",
            ["views"] = new JsonArray("schematic", "symbol"),
        });

        AssertSameDocument(
            mineCell,
            Cli("new", "cell", Path.Combine(cliDir, "Amp"), "Stage1", "--views", "schematic,symbol", "--json"),
            (mineDir, "<DEST>"), (cliDir, "<DEST>"));
    }

    /// <summary><c>import</c>, in its <c>convert</c> mode — an interchange round the GUI's own
    /// File ▸ Export drives too.</summary>
    [Fact]
    public void Import_ThroughTheServer_IsTheDocumentTheCliWrites()
    {
        string clay    = LayoutFixture();
        string mineOut = Path.Combine(Dir("mine"), "line.gds");
        string cliOut  = Path.Combine(Dir("theirs"), "line.gds");

        using var server = Start(Root);
        string mine = server.Call("import", new JsonObject
        {
            ["what"] = "convert", ["path"] = clay, ["output"] = mineOut,
        });

        AssertSameDocument(mine, Cli("convert", clay, "-o", cliOut, "--json"),
                           (mineOut, "<DEST>"), (cliOut, "<DEST>"));
    }

    /// <summary><c>read</c>, both of the things it reads: one of circuitRF's own documents, and a
    /// result file loaded back into cubes.</summary>
    [Fact]
    public void Read_ThroughTheServer_IsTheDocumentTheCliWrites()
    {
        string workspace = AuthorWorkspace("Amp");
        string csch      = Path.Combine(workspace, "Stage1", "schematic", "Stage1.csch");

        using var server = Start(Root);
        AssertSameDocument(server.Call("read", new JsonObject { ["path"] = csch }),
                           Cli("read", csch, "--json"));

        string touchstone = Path.Combine(CopyTestData("Hero1"), "potentially_unstable_amp.s2p");

        AssertSameDocument(server.Call("read", new JsonObject { ["path"] = touchstone }),
                           Cli("read", touchstone, "--json"));
    }

    /// <summary>
    /// R-aut5-6: narrowing is reachable as a tool argument, and it narrows the same way. Reading is
    /// the expensive direction — a client that receives eight full cubes when it wanted one is the
    /// failure mode the whole series is trying to avoid.
    /// </summary>
    [Fact]
    public void Narrowing_IsReachableThroughTheTool_AndNarrowsTheSameWay()
    {
        string touchstone = Path.Combine(CopyTestData("Hero1"), "potentially_unstable_amp.s2p");

        using var server = Start(Root);
        string mine = server.Call("read", new JsonObject
        {
            ["path"] = touchstone, ["only"] = new JsonArray("S"),
        });

        AssertSameDocument(mine, Cli("read", touchstone, "--only", "S", "--json"));

        var cubes = JsonDocument.Parse(mine).RootElement
            .GetProperty("result").GetProperty("groups").GetProperty("")
            .EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["S"], cubes);
    }

    /// <summary>
    /// A refusal is a document too, and it is the CLI's refusal — not one the adapter authored.
    /// The `--grid` a pursuit does not take is the case: the adapter forwards it and the VERB says no.
    /// </summary>
    [Fact]
    public void ARefusalFromTheVerb_ArrivesUnchanged_AndTheAdapterAuthoredNoneOfIt()
    {
        string cnl = Path.Combine(CopyTestData("Hero3B"), "hero3B_at_compression.cnl");
        string gam = Path.Combine(Dir("g"), "g.gam");

        using var server = Start(Root);
        string mine = server.Call("run", new JsonObject
        {
            ["analysis"] = "lpp", ["path"] = cnl, ["grid"] = gam,
        });

        AssertSameDocument(mine, Cli("lpp", cnl, "--grid", gam, "--json"));
        Assert.Contains("cli.args.grid-not-for-pursuit", Ids(mine));
    }

    /// <summary>
    /// <c>reference</c>, in all three of its forms — the topic list, one prose page, and one
    /// primitive out of the generated catalogue.
    ///
    /// <para>A reference read is a LARGE write, which is exactly the shape that finds a framing bug;
    /// the byte comparison here is what makes it worth running three times.</para>
    /// </summary>
    [Theory]
    [InlineData(null,         null)]
    [InlineData("netlist",    null)]
    [InlineData("components", "MLIN")]
    public void Reference_ThroughTheServer_IsTheDocumentTheCliWrites(string? topic, string? type)
    {
        var arguments = new JsonObject();
        if (topic is not null) arguments["topic"] = topic;
        if (type  is not null) arguments["type"]  = type;

        using var server = Start(Root);
        string mine = server.Call("reference", arguments);

        string[] argv = [.. new[] { "reference", topic, type }.Where(a => a is not null).Select(a => a!), "--json"];
        AssertSameDocument(mine, Cli(argv));
    }

    /// <summary>
    /// R-aut-13 for the OTHER channel. Every advertised resource returns the bytes
    /// <c>circuitrf reference &lt;topic&gt; --json</c> writes — a topic reachable one way and not the
    /// other fails here, and so does one whose two channels answer differently.
    ///
    /// <para>They are one code path with two envelopes, which is what makes the property structural
    /// rather than merely tested for; this is the test that says so out loud.</para>
    /// </summary>
    [Fact]
    public void EveryResource_ReturnsTheBytesTheCliWritesForItsTopic()
    {
        using var server = Start(Root);

        var resources = JsonNode.Parse(server.Request("resources/list", null))!["resources"]!.AsArray();
        Assert.NotEmpty(resources);

        foreach (var resource in resources)
        {
            string uri   = resource!["uri"]!.GetValue<string>();
            string topic = resource["name"]!.GetValue<string>();

            Assert.StartsWith("circuitrf://reference/", uri, StringComparison.Ordinal);
            Assert.EndsWith("/" + topic, uri, StringComparison.Ordinal);
            // The cost, published up front, for the same reason the CLI's topic list publishes it.
            Assert.True(resource["size"]!.GetValue<int>() > 0, $"{uri} advertised no size.");

            var read = JsonNode.Parse(server.Request("resources/read", new JsonObject { ["uri"] = uri }))!;
            var one  = read["contents"]!.AsArray().Single()!;

            Assert.Equal(uri, one["uri"]!.GetValue<string>());
            Assert.Equal("application/json", one["mimeType"]!.GetValue<string>());

            AssertSameDocument(one["text"]!.GetValue<string>(), Cli("reference", topic, "--json"));
        }
    }

    /// <summary>The tool and the resource are the same bytes for the same topic — asserted directly,
    /// so a client that reaches the surface through either gets one answer.</summary>
    [Fact]
    public void TheReferenceToolAndTheResource_AgreeOnTheSameTopic()
    {
        using var server = Start(Root);

        string viaTool = server.Call("reference", new JsonObject { ["topic"] = "units" });
        string viaResource = JsonNode.Parse(server.Request("resources/read", new JsonObject
        {
            ["uri"] = "circuitrf://reference/units",
        }))!["contents"]![0]!["text"]!.GetValue<string>();

        Assert.Equal(viaTool.Trim(), viaResource.Trim());
    }

    /// <summary>An unknown resource is answered with a DOCUMENT naming the ones that exist, not with
    /// a protocol error frame — the same shape an unknown topic gets on the command line
    /// (R-aut-7).</summary>
    [Fact]
    public void AnUnknownResource_IsRefusedWithADocumentNamingTheRealOnes()
    {
        using var server = Start(Root);

        var read = JsonNode.Parse(server.Request("resources/read", new JsonObject
        {
            ["uri"] = "circuitrf://reference/nosuchthing",
        }))!;

        Assert.Null(read["error"]);
        string document = read["contents"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("reference.resource.unknown", Ids(document));
        Assert.Contains("circuitrf://reference/netlist", document, StringComparison.Ordinal);
    }

    /// <summary>The capability is DECLARED. A resource surface a client is never told about is a
    /// resource surface nothing reads.</summary>
    [Fact]
    public void Initialize_DeclaresResources_AndSaysWhereTheReferenceIs()
    {
        using var server = Start(Root);
        var result = JsonNode.Parse(server.Request("initialize", new JsonObject()))!;

        Assert.NotNull(result["capabilities"]!["resources"]);
        Assert.Contains("reference", result["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ══ §5.2 — stdout purity ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-aut5-2. Every capability the server exposes, exercised with stdout captured, and not one
    /// byte outside the framing — including a real EM run and a run whose device model lives in a
    /// second process, which are the loudest paths this program has.
    ///
    /// <para>The framing is newline-delimited JSON-RPC, so the assertion is that EVERY line of
    /// stdout is a JSON-RPC message. A stray <c>Console.WriteLine</c> is its own unparseable line;
    /// a stray <c>Console.Write</c> corrupts the frame it prefixes. Both fail here.</para>
    /// </summary>
    [Fact]
    public void EveryCapability_WritesNothingToStdoutButTheFraming()
    {
        string kits      = DeviceWorkerKit();
        string workspace = AuthorWorkspace("Amp");
        string csch      = Path.Combine(workspace, "Stage1", "schematic", "Stage1.csch");
        string cem       = EmFixture();
        string clay      = LayoutFixture();
        string cnl       = WorkerNetlist();

        using (var server = Start(_root, kits))
        {
            server.Call("run",     new JsonObject { ["analysis"] = "dc", ["path"] = cnl });
            server.Call("run",     new JsonObject { ["analysis"] = "em", ["path"] = cem });
            server.Call("check",   new JsonObject { ["path"] = workspace });
            server.Call("explain", new JsonObject { ["path"] = csch });
            server.Call("create",  new JsonObject { ["what"] = "workspace", ["path"] = Path.Combine(Dir("more"), "B") });
            server.Call("import",  new JsonObject
            {
                ["what"] = "convert", ["path"] = clay, ["output"] = Path.Combine(Dir("more"), "line.gds"),
            });
            server.Call("read",    new JsonObject { ["path"] = csch });
            // RND-5's new capabilities, on the channel that would break first: a renderer that wrote
            // a progress line to Console.Out would corrupt the frame it prefixed and nothing else
            // would notice (gate 5). The attachment is asked for, because base64 is the largest
            // single thing this surface ever puts in a frame.
            server.Call("render",  new JsonObject
            {
                ["path"] = clay, ["output"] = Path.Combine(Dir("more"), "line.png"), ["attachImage"] = true,
            });
            server.Call("explain", new JsonObject { ["path"] = clay, ["extents"] = true });
            // The largest single write this surface makes, on both of its channels. A reference read
            // is exactly the shape that finds a framing bug (§6.7).
            server.Call("reference", new JsonObject());
            server.Call("reference", new JsonObject { ["topic"] = ReferenceLibrary.ComponentNotesTopic });
            server.Call("reference", new JsonObject { ["topic"] = "components" });
            server.Request("resources/list", null);
            server.Request("resources/read", new JsonObject { ["uri"] = "circuitrf://reference/netlist" });

            // Every line, including the notifications, and nothing else.
            foreach (string line in server.StdoutLines)
            {
                JsonNode? node = null;
                try { node = JsonNode.Parse(line); } catch (JsonException) { /* reported below */ }

                Assert.True(node is JsonObject obj && obj["jsonrpc"]?.GetValue<string>() == "2.0",
                    $"stdout carried something that is not a JSON-RPC frame:\n{Excerpt(line)}");
            }

            // …and the audit must actually have had something to audit.
            Assert.True(server.StdoutLines.Count >= 14,
                $"expected a frame per call; got {server.StdoutLines.Count}");

            // The proof the loud paths really were loud, and that all of it went to stderr where
            // §3.1 puts it. The EM run's own progress and its three lists are the noisiest thing here.
            Assert.Contains("note:", server.StdErr, StringComparison.Ordinal);
            output.WriteLine(server.StdErr.Length > 3000 ? server.StdErr[..3000] + " …" : server.StdErr);
        }
    }

    /// <summary>The em run really did run — otherwise the audit above measured a refusal.</summary>
    [Fact]
    public void AnEmRun_ThroughTheServer_WritesTheFilesTheCliWrites()
    {
        string cem = EmFixture();

        using var server = Start(Root);
        string mine = server.Call("run", new JsonObject { ["analysis"] = "em", ["path"] = cem });

        var doc = JsonDocument.Parse(mine).RootElement;
        Assert.Equal("ok", doc.GetProperty("status").GetString());

        var kinds = doc.GetProperty("outputs").EnumerateArray()
            .Select(o => o.GetProperty("kind").GetString()!).ToArray();
        Assert.Equal(["touchstone", "npy"], kinds);

        foreach (var o in doc.GetProperty("outputs").EnumerateArray())
            Assert.True(File.Exists(o.GetProperty("path").GetString()!),
                        $"outputs named a file that is not there: {o.GetProperty("path")}");
    }

    // ══ §5.3 — root confinement ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-aut5-8: three ways out of the root, each refused with a diagnostic that NAMES the root.
    /// A silent clamp is what this is refusing to be — a client asked to write one path and would
    /// have been handed another with nothing said about it.
    /// </summary>
    [Fact]
    public void APathLeavingTheRoot_IsRefused_NamingTheRoot()
    {
        string inside = Dir("inside");
        string beyond = Dir("beyond");
        File.WriteAllText(Path.Combine(beyond, "secret.cnl"), "; not yours\n");

        // A symlink INSIDE the root pointing out of it — the one escape a string comparison cannot
        // see, which is why both sides are resolved before the test.
        string link = Path.Combine(inside, "shortcut");
        try { Directory.CreateSymbolicLink(link, beyond); }
        catch (Exception ex) { output.WriteLine("symlink not available: " + ex.Message); link = ""; }

        using var server = Start(inside);

        foreach (string escape in new[]
                 {
                     "../beyond/secret.cnl",                      // traversal
                     Path.Combine(beyond, "secret.cnl"),          // absolute, outside
                     link.Length == 0 ? "" : "shortcut/secret.cnl", // through a link, outside
                 })
        {
            if (escape.Length == 0) continue;

            string document = server.Call("read", new JsonObject { ["path"] = escape });

            Assert.Contains("serve.path.outside-root", Ids(document));
            Assert.Equal(1, JsonDocument.Parse(document).RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(Real(inside), Message(document, "serve.path.outside-root"));
        }

        // The same call, staying inside, is not refused — or the three above would prove only that
        // `read` refuses everything.
        string ok = Path.Combine(inside, "ok.cnl");
        File.WriteAllText(ok, "; fine\n");
        Assert.DoesNotContain("serve.path.outside-root",
                              Ids(server.Call("read", new JsonObject { ["path"] = "ok.cnl" })));
    }

    /// <summary>The root is required, and a missing one is refused rather than defaulted. A default
    /// of "wherever the process was started" is a policy nobody stated.</summary>
    [Fact]
    public void WithoutARoot_ServeRefuses()
    {
        var (exitCode, _, stderr) = RunCli("serve");
        Assert.Equal(1, exitCode);
        Assert.Contains("--root", stderr, StringComparison.Ordinal);

        var (missing, stdout, _) = RunCli("serve", "--root", Path.Combine(_root, "not-there"), "--json");
        Assert.Equal(1, missing);
        Assert.Contains("serve.root.not-found", Ids(stdout));

        // …and `--json` on a serve that WOULD have started is itself refused, because it has already
        // taken the stdout the framing needs. Refused at that point and not before, so the two
        // refusals above still answer with a document the way every other verb's do.
        var (both, document, _) = RunCli("serve", "--root", Root, "--json");
        Assert.Equal(1, both);
        Assert.Contains("serve.args.json-not-applicable", Ids(document));
    }

    // ══ §5.4 — lifecycle ═════════════════════════════════════════════════════════════════════════

    /// <summary>It starts, advertises, and shuts down cleanly with no client ever calling a tool.</summary>
    [Fact]
    public void TheServer_StartsAdvertisesAndShutsDownCleanly()
    {
        using var server = Start(Root);

        var initialize = JsonDocument.Parse(server.Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
        })).RootElement;

        Assert.Equal("2025-06-18", initialize.GetProperty("protocolVersion").GetString());
        Assert.Equal("circuitrf",  initialize.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(initialize.GetProperty("capabilities").TryGetProperty("tools", out _));

        var tools = JsonDocument.Parse(server.Request("tools/list", null)).RootElement
            .GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToArray();

        // R-aut5-4: small and broad. THIRTEEN, and the count is asserted because the surface is a
        // standing cost paid on every interaction whether or not a tool is called. `reference` earns
        // its place by being reachable at all in a client that does not surface RESOURCES to the
        // model — which is where the same bytes are cheaper (R-aut6-4).
        //
        // `render` is RND-5's one addition, and it is ONE (R-rnd0-4): one tool over every document
        // kind, with the kind inferred from the path, rather than one per view type.
        //
        // `netlist`, `plot` and `find` are AUT-11's, and each is one tool for the same reason: the
        // document kind comes from the path. `netlist` is the one that changes what this surface can
        // DO rather than what it costs — without it nothing here could simulate a design anyone had
        // actually drawn.
        //
        // The last two are RC-5's (revision-control.md §5.3d, §5.3b). `history` is the CLI's own verb
        // like every tool above it. `batch` is the ONE tool that is not a command line: it holds
        // SESSION state — opened before an agent's first modification, closed when it is done — and a
        // process that exits after one command cannot hold that, which is why the architecture puts
        // it on this server and leaves the other three history nouns as verbs.
        //
        // `lvs` is brief-lvs-11-cli-verb.md's, and it is the one whose value here is greater than on
        // a command line: an agent that has just written a `.clay` CANNOT LOOK AT THE SCREEN, so
        // asking whether the artwork implements the drawing is the only way it can find out.
        Assert.Equal(["run", "check", "explain", "create", "import", "render", "netlist", "plot",
                      "find", "lvs", "read", "history", "reference", "batch"],
                     tools);

        Assert.Equal(0, server.Close());
    }

    /// <summary>Every advertised tool has a description and a schema a client can act on, and none
    /// of them is empty — an advertisement a client pays for and cannot use is worse than none.</summary>
    [Fact]
    public void EveryAdvertisedTool_CarriesADescriptionAndASchema()
    {
        using var server = Start(Root);

        foreach (var tool in JsonDocument.Parse(server.Request("tools/list", null)).RootElement
                     .GetProperty("tools").EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), name);

            var schema = tool.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());

            foreach (var property in schema.GetProperty("properties").EnumerateObject())
                Assert.False(
                    string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()),
                    $"{name}.{property.Name}");
        }
    }

    /// <summary>
    /// R-aut-1 and R-aut-13, at the level the per-tool parity gate above cannot reach: **every
    /// argument the catalog advertises is a flag the verb behind it actually reads.**
    ///
    /// <para><b>Why this is its own gate.</b> The parity tests each make ONE call per tool, so they
    /// compare the two adapters on the arguments that call happens to pass and say nothing about
    /// the rest of the table. Three advertised arguments were not flags at all — <c>run</c>'s
    /// <c>sparam</c> offered <c>-a</c> and <c>--set</c>, its <c>dc</c> offered <c>--set</c>,
    /// <c>--tol</c>, <c>--maxharm</c> and <c>--maxmix</c>, and both loadpull modes offered
    /// <c>--maxmix</c> — none of which the verb's own argument loop reads.</para>
    ///
    /// <para><b>And the failure was not "unknown option".</b> A run verb finds its input by "the
    /// first token that does not start with a dash", so a dropped flag's VALUE became the input
    /// path: <c>lp x.cnl --maxmix 3</c> answered <c>File not found: 3</c>. Where the path was read
    /// positionally instead, as <c>dc</c> reads it, nothing was reported at all and the run answered
    /// a different question than the one asked. Both are now refusals
    /// (<c>CliDiagnostics.RunUnknownOption</c>), which is what lets this test be a behavioural one
    /// rather than a source scan: it asks the real server for the real schema and calls every
    /// argument in it.</para>
    ///
    /// <para>The path given is deliberately one that does not exist. Every verb's argument loop runs
    /// to completion before it looks at the file, so nothing is read, run or written — and the
    /// refusal a missing file produces cannot mask an unknown-option refusal, because both are in
    /// the same document.</para>
    /// </summary>
    [Fact]
    public void EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads()
    {
        // The positionals each mode requires, which a schema advertising the UNION across modes
        // cannot say. A mode absent from here fails below rather than being skipped: a new mode is
        // exactly when this gate is worth being reminded of.
        var positionals = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["run/sparam"]        = ["path"],
            ["run/dc"]            = ["path"],
            ["run/hb"]            = ["path"],
            ["run/lp"]            = ["path"],
            ["run/lpp"]           = ["path"],
            ["run/em"]            = ["path"],
            ["check/"]            = ["path"],
            ["explain/"]          = ["path"],
            ["create/workspace"]  = ["path"],
            ["create/cell"]       = ["workspace", "name"],
            ["import/part"]       = ["path"],
            ["import/convert"]    = ["path"],
            ["read/"]             = ["path"],
            // AUT-11's three. Single-mode for the same reason `render` is: each takes a path whose
            // kind it infers, so there is no selector to key on.
            ["netlist/"]          = ["path"],
            ["plot/"]             = ["path"],
            ["find/"]             = ["path"],
            // RND-5's one new tool. Single-mode for R-rnd5-2's reason: the document kind comes from
            // the path exactly as `check`'s does, so there is no selector to key on.
            ["render/"]           = ["path"],
            // brief-lvs-11-cli-verb.md R-lvs11-5a. Single-mode, like `check` and `render`: the
            // document kind comes from the path, so there is no selector to key on.
            ["lvs/"]              = ["path"],
            // `reference` has no REQUIRED positional at all: its no-argument form is the topic list,
            // which is a real answer rather than a usage error. Both of its positionals are therefore
            // probed as arguments below, which is what this gate is for.
            ["reference/"]        = [],
            // RC-5's three history nouns (revision-control.md §5.3d) — CLI verbs like every other
            // row here, so the same gate applies to them.
            ["history/checkpoint"] = ["path"],
            ["history/list"]       = ["path"],
            ["history/restore"]    = ["path"],
        };

        using var server = Start(Root);
        var tools = JsonNode.Parse(server.Request("tools/list", null))!["tools"]!.AsArray();

        var covered = new HashSet<string>(StringComparer.Ordinal);
        int probes = 0;
        int adapterArguments = 0;

        foreach (var tool in tools)
        {
            string name       = tool!["name"]!.GetValue<string>();

            // RC-5's `batch` is the one tool that does not become a command line: it holds session
            // state this process owns, so there is no verb whose argument loop could be scanned. Its
            // own gate is that the state and the ten rules come back in the surface's output, which
            // RestoreAndBatchTests asserts.
            if (name == "batch") continue;

            var    properties = tool["inputSchema"]!["properties"]!.AsObject();

            // The selector is the one property with an enum — the same shape ToolCatalog builds it.
            var selector = properties.FirstOrDefault(p => p.Value?["enum"] is JsonArray);
            string[] modes = selector.Value?["enum"] is JsonArray values
                ? [.. values.Select(v => v!.GetValue<string>())]
                : [""];

            foreach (string mode in modes)
            {
                string key = $"{name}/{mode}";
                Assert.True(positionals.TryGetValue(key, out string[]? required),
                            $"'{key}' is a mode this gate does not know the positionals of. Add it.");
                covered.Add(key);

                foreach (var (argument, schema) in properties)
                {
                    if (argument == selector.Key || required!.Contains(argument)) continue;

                    // The one deliberate exemption, and it is by NAME rather than by a pattern
                    // (ToolSpec.Adapter). `attachImage` asks whether the tool result carries the
                    // rendered file back as protocol content — a property of the envelope, not of the
                    // render, with no CLI spelling and no business having one. Its own gates are
                    // TheAttachedPicture_IsTheFileOnDisk_ByteForByte and the cap test beside it;
                    // exempting it here would be free if it were never asserted anywhere, so it is
                    // counted and the count is checked below.
                    if (argument == ToolCatalog.AttachImageArgument) { adapterArguments++; continue; }

                    var arguments = new JsonObject();
                    if (selector.Key is not null) arguments[selector.Key] = mode;
                    foreach (string p in required!)
                        arguments[p] = Path.Combine(Root, $"probe-{probes}-{p}");
                    arguments[argument] = Plausible(schema!);

                    string document = server.Call(name, arguments);
                    probes++;

                    var ids = Ids(document);

                    // The argument belongs to another mode of the same tool. That refusal is the
                    // adapter's own and is correct; there is nothing to check about the verb.
                    if (ids.Contains("serve.args.not-for-mode")) continue;

                    Assert.DoesNotContain(ids, id => id.EndsWith(".args.unknown-option",
                                                                 StringComparison.Ordinal));

                    // The other half of the same defect: the value taken as a second input path.
                    Assert.DoesNotContain("cli.args.multiple-inputs", ids);
                    Assert.DoesNotContain("convert.args.multiple-inputs", ids);
                    Assert.DoesNotContain("render.args.multiple-paths", ids);
                }
            }
        }

        Assert.Equal(positionals.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     covered.OrderBy(k => k, StringComparer.Ordinal));

        // Not vacuous: a loop that advertised nothing would pass every assertion above.
        Assert.True(probes > 60, $"only {probes} arguments were probed");

        // R-rnd5-1, gate 2: the new rows are covered by this gate AUTOMATICALLY, which is the point
        // of the table — so assert it actually exercised them rather than trusting that it would.
        // `render` alone is ~24 arguments, and `explain` gained five.
        var renderRow = ToolCatalog.Tools.Single(t => t.Name == "render");
        Assert.True(probes >= 60 + renderRow.Modes[0].Options.Length,
                    $"{probes} probes cannot have covered render's {renderRow.Modes[0].Options.Length} arguments");
        foreach (string flag in new[] { "--cells", "--layers", "--extents", "--all", "--view" })
            Assert.Contains(ToolCatalog.Tools.Single(t => t.Name == "explain").Modes[0].Options,
                            o => o.Cli == flag);

        // …and the exemption was used exactly as often as there are adapter arguments to exempt: one
        // per mode of the one tool that has one. An exemption that silently grew would be a flag the
        // verb does not read, back again by the other door.
        Assert.Equal(ToolCatalog.Tools.Sum(t => (t.Adapter?.Length ?? 0) * Math.Max(1, t.Modes.Length)),
                     adapterArguments);
    }

    /// <summary>A syntactically valid value of the schema's own type. It does not have to be
    /// MEANINGFUL — a refusal about the value is fine and is not what this gate reads.</summary>
    private static JsonNode Plausible(JsonNode schema) => schema["type"]!.GetValue<string>() switch
    {
        "boolean" => JsonValue.Create(true),
        "integer" => JsonValue.Create(1),
        "number"  => JsonValue.Create(1.0),
        "array"   => new JsonArray("x"),
        // AUT-12's `StrMap` — a map of name to value, which the adapter type-checks before it emits
        // anything. Falling through to the string default here would make the probe a wrong-type
        // refusal, which passes every assertion below while exercising nothing.
        "object"  => new JsonObject { ["x"] = "x" },
        _         => JsonValue.Create("x"),
    };

    /// <summary>
    /// A client that goes away with work outstanding. Every pending call is cancelled and the
    /// process ends by ITSELF — a server that survived the client only to linger would be a leak on
    /// every disconnect, and one that had to be killed would leave a solve running behind it.
    ///
    /// <para>Two calls are sent so one is genuinely still queued when the pipe closes; see the
    /// cancellation gate above for why the in-flight case is pinned through the queue rather than
    /// through a stopwatch.</para>
    /// </summary>
    [Fact]
    public void AClientThatDisconnects_CancelsWhatIsOutstanding_AndTheProcessEnds()
    {
        string cem = EmFixture(points: 400);

        var server = Start(Root);
        server.Request("initialize", new JsonObject());
        server.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = 99,
            ["method"]  = "tools/call",
            ["params"]  = new JsonObject
            {
                ["name"]      = "run",
                ["arguments"] = new JsonObject { ["analysis"] = "em", ["path"] = cem },
            },
        });

        // Long enough that the run is genuinely under way, on a sweep long enough that it cannot
        // have finished — and then the pipe simply closes, which is what a dead client looks like.
        Thread.Sleep(750);

        int exitCode = server.Close(TimeSpan.FromSeconds(60));
        Assert.Equal(0, exitCode);
        server.Dispose();
    }

    /// <summary>An unknown method is an error frame, not a dead connection; and the session carries
    /// on afterwards, which is the part that matters.</summary>
    [Fact]
    public void AnUnknownMethod_IsAnErrorFrame_AndTheSessionContinues()
    {
        using var server = Start(Root);
        server.Request("initialize", new JsonObject());

        var error = JsonDocument.Parse(server.RequestRaw("no/such/method", null)).RootElement
            .GetProperty("error");
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());

        Assert.Contains("run", server.Request("tools/list", null));
    }

    /// <summary>
    /// R-aut5-8: a long run reports progress, through the same <c>RunControl</c> the <c>em</c> verb
    /// already uses. Progress is opt-in per call — a client that asks for it by sending a progress
    /// token gets it, and one that does not is not sent notifications it never asked for.
    /// </summary>
    [Fact]
    public void ALongRun_ReportsProgress_WhenTheClientAsksForIt()
    {
        string cem = EmFixture(points: 4000);

        using var server = Start(Root);
        server.Request("initialize", new JsonObject());

        int id = server.SendRequest("tools/call", new JsonObject
        {
            ["name"]      = "run",
            ["arguments"] = new JsonObject { ["analysis"] = "em", ["path"] = cem },
        }, meta: new JsonObject { ["progressToken"] = "t-1" });

        string document = ServeSession.DocumentOf(server.Await(id, "run em"));
        Assert.Equal("ok", JsonDocument.Parse(document).RootElement.GetProperty("status").GetString());

        var progress = server.Notifications("notifications/progress");
        Assert.NotEmpty(progress);

        foreach (var note in progress)
        {
            Assert.Equal("t-1", note["params"]!["progressToken"]!.GetValue<string>());
            Assert.True(note["params"]!["progress"]!.GetValue<long>() >= 0);
        }

        // …and it moved. A single observation repeated is a bar that never advances, which is what
        // a client with no progress at all already has.
        Assert.True(progress.Select(n => n["params"]!["progress"]!.GetValue<long>()).Distinct().Count() > 1,
                    "progress was reported but never advanced");
    }

    /// <summary>
    /// R-aut5-8: a call is cancellable, and a cancelled one is reported as stopped rather than as a
    /// result. <c>notifications/cancelled</c> finds the request, its token reaches the same
    /// <c>RunControl</c> the <c>em</c> verb already uses, and the verb answers with 130 — the code
    /// <c>cli.md</c> §7 already gives a run stopped at a work boundary. The adapter picks no number
    /// of its own.
    ///
    /// <para><b>The cancelled call is the QUEUED one, and that is deliberate rather than a
    /// convenience.</b> Capability calls are serialized (the verbs use process-wide state), so the
    /// second of two arrives while the first is running — which is exactly the case a client hits
    /// when it changes its mind. Cancelling the one already in flight would need a capability that
    /// runs long enough to catch, and in the default test tier there is none: every analysis a
    /// non-benchmark fixture can express finishes in well under a second, and the one thing that
    /// genuinely takes minutes — a de-embedded full-wave point — belongs to
    /// <c>Category=Benchmark</c>. A timing race dressed up as a gate would measure the machine
    /// (feedback-no-new-timing-benchmark-tests), so what is pinned here is the mechanism: the token
    /// reaches the run, the run refuses to produce a result, and 130 comes back.</para>
    /// </summary>
    [Fact]
    public void ACancelledCall_IsStopped_AndReportedAsStoppedRatherThanAsAResult()
    {
        string cem = EmFixture(points: 400);

        using var server = Start(Root);
        server.Request("initialize", new JsonObject());

        int first  = server.SendRequest("tools/call", RunEm(cem));
        int second = server.SendRequest("tools/call", RunEm(cem));

        server.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = "notifications/cancelled",
            ["params"]  = new JsonObject { ["requestId"] = second, ["reason"] = "changed my mind" },
        });

        // The first ran to completion — a cancellation is aimed at one request, not at the session.
        var ran = JsonDocument.Parse(ServeSession.DocumentOf(server.Await(first, "the first run"))).RootElement;
        Assert.Equal(0, ran.GetProperty("exitCode").GetInt32());

        var stopped = JsonDocument.Parse(ServeSession.DocumentOf(server.Await(second, "the cancelled run"))).RootElement;

        Assert.Equal(130, stopped.GetProperty("exitCode").GetInt32());
        Assert.Equal("failed", stopped.GetProperty("status").GetString());

        // Nothing was written. A cancelled run abandons its result rather than publishing a partial
        // one — RunControl's own contract — so the document must not name a file a caller would then
        // go looking for.
        Assert.Empty(stopped.GetProperty("outputs").EnumerateArray());

        // The session is still usable: a cancellation is not a disconnection.
        Assert.Contains("run", server.Request("tools/list", null));

        static JsonObject RunEm(string cem) => new()
        {
            ["name"]      = "run",
            ["arguments"] = new JsonObject { ["analysis"] = "em", ["path"] = cem },
        };
    }

    // ══ fixtures ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>A copy of one of the repo's own netlist fixtures, under the server root. The whole
    /// folder, because a netlist references its data files by a path relative to itself.</summary>
    private string CopyTestData(string name)
    {
        string source      = Path.Combine(RepoRoot(), "testdata", name);
        string destination = Path.Combine(Dir("testdata"), name);
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        return destination;
    }

    private string Dir(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A workspace with one cell, authored through the CLI itself — the shortest route to a
    /// tree that is correct by the application's own standard rather than by this file's.</summary>
    private string AuthorWorkspace(string name)
    {
        string workspace = Path.Combine(Dir("author"), name);
        Assert.Equal(0, RunCli("new", "workspace", workspace).ExitCode);
        Assert.Equal(0, RunCli("new", "cell", workspace, "Stage1", "--views", "schematic,symbol").ExitCode);
        return workspace;
    }

    /// <summary>A kit folder whose worker is the reference device worker, built alongside these
    /// tests. A real second process, because auditing a fake would audit the fake.</summary>
    private string DeviceWorkerKit()
    {
        string kits = Path.Combine(Dir("kits"), "ExampleKit");
        Directory.CreateDirectory(kits);

        string workerDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ServeProtocolAdapterTests).Assembly)
            .First(a => a.Key == "DeviceWorkerExampleDir").Value!;

        string command = Path.GetFullPath(Path.Combine(
            workerDir, OperatingSystem.IsWindows() ? "DeviceWorkerExample.exe" : "DeviceWorkerExample"));

        Assert.True(File.Exists(command), $"the reference device worker was not built: {command}");

        File.WriteAllText(Path.Combine(kits, "device-provider.json"), $$"""
            { "provider": "ExampleKit",
              "workers": [ { "platform": "any", "command": {{JsonSerializer.Serialize(command)}} } ] }
            """);

        return Path.GetDirectoryName(kits)!;
    }

    /// <summary>A DC netlist whose one active device is evaluated by that worker.</summary>
    private string WorkerNetlist()
    {
        string cnl = Path.Combine(Dir("fet"), "fet.cnl");
        File.WriteAllText(cnl, """
            Vdc:VG  g 0  Vdc=2
            Vdc:VD  d 0  Vdc=3
            R:RG    g 0  R=1e6
            ExtDevice:X1  g d 0  Provider="ExampleKit" Type="example_fet_v1" Beta=0.02 Vth=1.0 Lambda=0.02 Cgs=1e-12 Ggs=1e-9

            analysis DC1 type=dc
            """);
        return cnl;
    }

    /// <summary>A `.clay` with one rectangle, and the technology it reads through.</summary>
    private string LayoutFixture()
    {
        string dir = Dir("art");
        TechPersistence.SaveToFile(Path.Combine(dir, "pcb.ctech"), StarterTechnologies.Pcb2Layer());
        WorkspacePersistence.SaveToFile(Path.Combine(dir, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });

        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });

        string clay = Path.Combine(dir, "Line.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return clay;
    }

    /// <summary>
    /// A layout whose vector rendering is comfortably past the attachment cap — one polygon of
    /// 250,000 vertices, which at <c>--detail full</c> an SVG stores every one of.
    ///
    /// <para>It is a REAL render rather than a stubbed file size, because the cap is about what this
    /// verb actually produces: <c>src/Cli/RESOLVED.md</c> measures a real 6-layer board at 23.5 MB of
    /// SVG at <c>--detail full</c> and 6.2 MB at <c>--detail screen</c>, and the point of the cap is
    /// that both of those are past it while every PNG of the same board is not.</para>
    /// </summary>
    private string HugeLayoutFixture()
    {
        string dir = Dir("bigart");
        TechPersistence.SaveToFile(Path.Combine(dir, "pcb.ctech"), StarterTechnologies.Pcb2Layer());
        WorkspacePersistence.SaveToFile(Path.Combine(dir, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });

        var view = new LayoutView { DbuPerMicron = 1000 };

        const int n = 250_000;
        var xy = new long[2 * n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            // A slowly wandering radius, so no two consecutive vertices coincide once the coordinates
            // are quantized — a degenerate ring would decimate to nothing and measure the decimator.
            double r = 9_000_000 + 900_000 * Math.Sin(37 * a);
            xy[2 * i]     = (long)(10_000_000 + r * Math.Cos(a));
            xy[2 * i + 1] = (long)(10_000_000 + r * Math.Sin(a));
        }
        view.Shapes.Add(new PolygonShape { Layer = new(1, 0), Xy = xy });

        string clay = Path.Combine(dir, "Huge.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return clay;
    }

    /// <summary>
    /// A workspace holding a line and the `.cem` that extracts it — EmCliVerbTests' own fixture
    /// shape.
    ///
    /// <para><paramref name="points"/> is how long the run takes. Three is a run and not a benchmark,
    /// which is what the parity and purity gates want. The progress and cancellation gates want the
    /// opposite — a run that is genuinely still going when the next message arrives — and they ask
    /// for many. Those two are FAST WHEN CORRECT and slow only when they are about to fail: a
    /// cancellation that works stops within a point, and one that does not runs the whole sweep.</para>
    /// </summary>
    private string EmFixture(int points = 3)
    {
        string root = Dir("em");
        string cellLayoutDir = Path.Combine(root, "Line", "layout");
        Directory.CreateDirectory(cellLayoutDir);

        TechPersistence.SaveToFile(Path.Combine(root, "pcb.ctech"), StarterTechnologies.Pcb2Layer());
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });

        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        LayoutPersistence.SaveToFile(Path.Combine(cellLayoutDir, "Line.clay"), view);

        string cem = Path.Combine(root, "line.cem");
        EmSetupPersistence.SaveToFile(cem, new EmSetup
        {
            Name      = "line",
            LayoutRef = Path.Combine("Line", "layout", "Line.clay"),
            Frequency = new FrequencySpec("1", "10", points, SweepKind.Linear, "GHz", "GHz"),
        });
        return cem;
    }

    // ══ assertions ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The parity assertion (R-aut-13). Byte for byte, after substituting only what is legitimately
    /// variable — see this file's header for why that list is two items long and not longer.
    /// </summary>
    private void AssertSameDocument(string throughTheServer, CliRun throughTheCli,
                                    params (string From, string To)[] normalize)
    {
        output.WriteLine("cli stderr:\n" + Excerpt(throughTheCli.StdErr));

        string mine   = throughTheServer.Trim();
        string theirs = throughTheCli.StdOut.Trim();

        foreach (var (from, to) in normalize)
        {
            mine   = mine.Replace(from, to, StringComparison.Ordinal);
            theirs = theirs.Replace(from, to, StringComparison.Ordinal);
        }

        Assert.False(mine.Length == 0, "the server returned no document at all");
        Assert.Equal(theirs, mine);
    }

    private static string[] Ids(string document) =>
        [.. JsonDocument.Parse(document).RootElement.GetProperty("diagnostics").EnumerateArray()
             .Select(d => d.GetProperty("id").GetString()!)];

    private static string Message(string document, string id) =>
        JsonDocument.Parse(document).RootElement.GetProperty("diagnostics").EnumerateArray()
            .First(d => d.GetProperty("id").GetString() == id)
            .GetProperty("message").GetString()!;

    /// <summary>The path with its symlinks followed — what the refusal names, since that is what the
    /// server compared against.</summary>
    private static string Real(string path) =>
        new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
        ?? Path.GetFullPath(path);

    private static string Excerpt(string s) => s.Length > 2000 ? s[..2000] + " …" : s;

    // ══ the server, as a process ═════════════════════════════════════════════════════════════════

    private ServeSession Start(string root, string? kits = null) => new(CliDll(), RepoRoot(), root, kits, output);

    /// <summary>
    /// One <c>circuitrf serve</c> process, driven over its own pipes.
    ///
    /// <para>stdin is held OPEN for the life of the session, which is not a detail: closing it is how
    /// a client disconnects, and a driver that wrote its requests and closed would cancel every run
    /// it had just asked for.</para>
    ///
    /// <para>stderr is drained on its own thread throughout. An EM run says enough there to fill the
    /// pipe's buffer, and a sequential reader deadlocks against it — the same trap EmCliVerbTests
    /// records.</para>
    /// </summary>
    private sealed class ServeSession : IDisposable
    {
        private readonly Process                       _proc;
        private readonly List<string>                  _stdout = [];
        private readonly StringBuilder                 _stderr = new();
        private readonly System.Collections.Concurrent.BlockingCollection<string> _frames = new();
        private readonly Task                          _outPump;
        private readonly Task                          _errPump;
        private readonly ITestOutputHelper             _output;
        private int _nextId = 1;

        public ServeSession(string cliDll, string workingDirectory, string root, string? kits,
                            ITestOutputHelper output)
        {
            _output = output;

            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory       = workingDirectory,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            psi.ArgumentList.Add(cliDll);
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(root);
            if (kits is not null) { psi.ArgumentList.Add("--kits"); psi.ArgumentList.Add(kits); }

            _proc = Process.Start(psi)!;

            // BOTH pipes are drained continuously, on their own threads, and this is not a style
            // choice. Reading stdout only when an answer is wanted deadlocks the moment the server
            // has more to say than the pipe will buffer — a result document for a 400-point sweep is
            // comfortably past that — and the symptom is a server that appears to hang on shutdown.
            //
            // LongRunning, so each pump gets a DEDICATED thread rather than a thread-pool slot. Under
            // a full-solution run the pool is saturated and a pooled pump can sit queued for the
            // whole session — which is not a hang (the other pump keeps the process moving) but a
            // silent empty capture: the stdout audit passed while `StdErr` read "" and the assertion
            // that the loud paths really were loud failed with nothing to look at.
            _outPump = Task.Factory.StartNew(() =>
            {
                string? line;
                while ((line = _proc.StandardOutput.ReadLine()) is not null)
                {
                    lock (_stdout) _stdout.Add(line);
                    _frames.Add(line);
                }
                _frames.CompleteAdding();
            }, TaskCreationOptions.LongRunning);

            _errPump = Task.Factory.StartNew(() =>
            {
                string? line;
                while ((line = _proc.StandardError.ReadLine()) is not null)
                    lock (_stderr) _stderr.AppendLine(line);
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>Every line stdout carried, in order — the §5.2 audit reads this.</summary>
        public IReadOnlyList<string> StdoutLines { get { lock (_stdout) return [.. _stdout]; } }

        public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }

        public void Send(JsonObject message)
        {
            _proc.StandardInput.Write(message.ToJsonString());
            _proc.StandardInput.Write('\n');
            _proc.StandardInput.Flush();
        }

        /// <summary>Sends a request and returns its <c>result</c>, as JSON text.</summary>
        public string Request(string method, JsonObject? parameters) =>
            JsonNode.Parse(RequestRaw(method, parameters))!["result"]!.ToJsonString();

        /// <summary>Sends a request and returns the whole frame, error or result.</summary>
        public string RequestRaw(string method, JsonObject? parameters) =>
            Await(SendRequest(method, parameters), method);

        /// <summary>Sends a request WITHOUT waiting for it, and returns its id. What a client does
        /// when it intends to send something else — a cancellation — while the answer is pending.</summary>
        public int SendRequest(string method, JsonObject? parameters, JsonObject? meta = null)
        {
            int id = _nextId++;
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                if (meta is not null) parameters["_meta"] = meta;
                message["params"] = parameters;
            }
            Send(message);
            return id;
        }

        /// <summary>Reads until the frame carrying <paramref name="id"/> arrives. Notifications —
        /// progress, above all — are interleaved with the answer, so the answer is the first frame
        /// carrying THIS id rather than simply the next frame.</summary>
        public string Await(int id, string what = "a request")
        {
            while (true)
            {
                Assert.True(_frames.TryTake(out string? line, (int)TimeSpan.FromMinutes(2).TotalMilliseconds),
                            $"the server ended or fell silent before answering '{what}'.\n{StdErr}");

                var frame = JsonNode.Parse(line!) as JsonObject;
                if (frame?["id"]?.GetValue<int>() == id) return line!;
            }
        }

        /// <summary>Every notification of one method the session has seen so far.</summary>
        public IReadOnlyList<JsonObject> Notifications(string method) =>
            [.. StdoutLines.Select(l => JsonNode.Parse(l) as JsonObject)
                 .Where(f => f?["method"]?.GetValue<string>() == method)
                 .Select(f => f!)];

        /// <summary>The document a completed <c>tools/call</c> frame carries.</summary>
        public static string DocumentOf(string frame) =>
            JsonNode.Parse(frame)!["result"]!["content"]![0]!["text"]!.GetValue<string>();

        /// <summary>Calls a tool and returns the document it handed back — which is the whole of what
        /// a tool returns (R-aut5-5).</summary>
        public string Call(string tool, JsonObject arguments) => CallRaw(tool, arguments)[0]!["text"]!.GetValue<string>();

        /// <summary>Calls a tool and returns its whole <c>content</c> array. RND-5's attachment is an
        /// ADDITIONAL block beside the document, so a caller that only ever reads block 0 cannot see
        /// it — nor see that it was not sent.</summary>
        public JsonArray CallRaw(string tool, JsonObject arguments)
            => JsonNode.Parse(Request("tools/call", new JsonObject
               {
                   ["name"] = tool, ["arguments"] = arguments,
               }))!["content"]!.AsArray();

        /// <summary>Closes stdin — a client disconnecting — and waits for the process to end.</summary>
        public int Close(TimeSpan? within = null)
        {
            try { _proc.StandardInput.Close(); } catch { /* already gone */ }

            if (!_proc.WaitForExit((int)(within ?? TimeSpan.FromSeconds(30)).TotalMilliseconds))
            {
                _output.WriteLine("serve did not exit; stderr:\n" + StdErr);
                try { _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return -1;
            }

            _outPump.Wait(TimeSpan.FromSeconds(5));
            _errPump.Wait(TimeSpan.FromSeconds(5));
            return _proc.ExitCode;
        }

        public void Dispose()
        {
            if (!_proc.HasExited) Close();
            _proc.Dispose();
        }
    }

    // ══ the CLI, as a process ════════════════════════════════════════════════════════════════════

    private readonly record struct CliRun(int ExitCode, string StdOut, string StdErr);

    private CliRun Cli(params string[] args) => RunCli(args);

    private CliRun RunCli(params string[] args)
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
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return new CliRun(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(ServeProtocolAdapterTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
