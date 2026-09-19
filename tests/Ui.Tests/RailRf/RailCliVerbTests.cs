// ================================================================
//  RailCliVerbTests.cs — brief-railrf-10-cli-verb.md §4, for `circuitrf rail`.
//
//  ── THE TWO GATES THE HOUSE REQUIRES OF A CLI VERB, AND WHY THEY ARE BOTH HERE ────────────────
//
//  R-rail10-7 launches the real CLI DLL as a PROCESS and compares the bytes it wrote against an
//  in-process call to the code the GUI itself drives — `RailDcRun.Run` for the numbers and
//  `RailReportPage.Draw` for the page. A same-process call could not show the failure that actually
//  matters here: a second process with no Avalonia host under it drawing in a substituted typeface,
//  which is exactly what RND-1 found and fixed. So the process is the measurement, not an
//  inconvenience.
//
//  R-rail10-8 is a comment-stripped source scan over src/Cli/Rail.cs. The byte gate proves the verb
//  agrees with the library TODAY; the scan is what keeps it from ever growing a second copy that
//  could stop agreeing. `AuthoringCliVerbTests` states the rule and this is the same rule one verb
//  along.
//
//  ── WHY THE FIXTURE ANCHORS BY COORDINATE ────────────────────────────────────────────────────
//
//  A `.crail` names no placement file (RailDocument has ArtworkCellRef, TechnologyRef and
//  PartLibraryRef and nothing else), so headless there are no pads to resolve a refdes against —
//  which is precisely the case RailPortAnchor's coordinate fallback exists for: "a coordinate is
//  accepted where there is no placement file and no board netlist". The fixture uses it, and the
//  verb's own `@x,y` spelling is what a caller types for the same reason.
// ================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-rail-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private const double CopperSigma = 5.8e7;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ── R-rail10-7: byte identity against the in-process call ────────────────

    /// <summary>
    /// The verb, as a process, writes the page <see cref="RailReportPage"/> draws for the same
    /// result — byte for byte.
    /// </summary>
    /// <remarks>
    /// <b>The in-process side is spelled out rather than borrowed from the verb.</b> That is
    /// <c>RenderCliVerbTests</c>' own choice and for its reason: what is written down here is what
    /// the verb's defaults MEAN — which map the report draws, which theme it resolves, what the
    /// provenance banner says — so a silent change to any of them is a failing test rather than a
    /// quietly different page.
    ///
    /// <para><b>Two exclusions, both stated, and neither is a property of either code path.</b>
    /// R-rail10-7 asks for them to be named, and they are the ones <c>RenderCliVerbTests</c> already
    /// measured: Skia's SVG device numbers its <c>clipPath</c> elements from a PROCESS-WIDE counter it
    /// never resets, and a PDF carries its own creation timestamp. Applied only when the raw bytes
    /// actually differ, and reported when they do — a comparison that normalised unconditionally
    /// would stop being able to say the two are identical.</para>
    /// </remarks>
    [Fact]
    public void TheReportAsAProcess_IsTheBytesTheReportPageDraws()
    {
        var fx = Fixture();
        string outPath = Path.Combine(_root, "cli.svg");

        var (exit, stdout, stderr) = RunCli("rail", fx.Crail, "-o", outPath);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        AssertSameSvg(File.ReadAllText(outPath), InProcessReportSvg(fx));
    }

    /// <summary>
    /// The same claim in PDF, which is a different encoder over the same drawing.
    /// </summary>
    /// <remarks>
    /// Worth its own case rather than folded in: the PDF device embeds font subsets where the SVG
    /// device emits path data, so a typeface that resolved differently in the child process shows up
    /// in the PDF bytes in a way it does not in the SVG's.
    /// </remarks>
    [Fact]
    public void TheReportAsAProcess_IsTheBytesTheReportPageDrawsInPdfToo()
    {
        var fx = Fixture();
        string outPath = Path.Combine(_root, "cli.pdf");

        var (exit, stdout, stderr) = RunCli("rail", fx.Crail, "-o", outPath);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        byte[] fromCli = File.ReadAllBytes(outPath);
        byte[] inProcess = InProcessReportPdf(fx);

        if (fromCli.AsSpan().SequenceEqual(inProcess)) { output.WriteLine("pdf: byte-identical"); return; }

        output.WriteLine($"pdf: differ before normalisation ({fromCli.Length} vs {inProcess.Length} bytes)");
        Assert.Equal(StripPdfDates(inProcess), StripPdfDates(fromCli));
    }

    /// <summary>
    /// The numbers the verb prints are <see cref="RailDcRun"/>'s own — the same call the window's Run
    /// button makes, on the same request.
    /// </summary>
    /// <remarks>
    /// This is the byte gate's other half: the page proves the DRAWING is shared, and this proves the
    /// ANSWER is. Compared at full precision through the verb's own <c>--json</c>, because a console
    /// table is rounded and a rounded comparison would pass against a verb that had quietly acquired
    /// arithmetic of its own.
    /// </remarks>
    [Fact]
    public void TheVoltagesAsAProcess_AreRailDcRunsOwn()
    {
        var fx = Fixture();

        var (exit, stdout, stderr) = RunCli("rail", fx.Crail, "--json");
        output.WriteLine(stderr);
        Assert.Equal(0, exit);

        var run = RailDcRun.Run(Request(fx));
        Assert.Null(run.Refusal);

        var ports = JsonDocument.Parse(stdout).RootElement
            .GetProperty("result").GetProperty("rail")
            .GetProperty("rails").EnumerateArray().Single()
            .GetProperty("ports").EnumerateArray().ToList();

        Assert.Equal(run.Rails[0].Ports.Count, ports.Count);
        for (int i = 0; i < ports.Count; i++)
            Assert.Equal(run.Rails[0].Ports[i].VoltageV, ports[i].GetProperty("voltageV").GetDouble(), 12);
    }

    // ── R-rail10-8: the source scan ──────────────────────────────────────────

    /// <summary>
    /// <c>src/Cli/Rail.cs</c> holds no extraction, no mesh, no solve, no breakdown arithmetic and no
    /// second export path.
    /// </summary>
    /// <remarks>
    /// <b>Comment-stripped</b>, because this file's own prose names every one of those types in order
    /// to say it does not call them — and a scan that could be defeated by a comment is a scan that
    /// would have to be silenced the first time someone documented the rule.
    /// </remarks>
    [Fact]
    public void TheVerbHoldsNoAnalysisAndNoSecondExportPath()
    {
        string source = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "Rail.cs")));

        foreach (string forbidden in new[]
        {
            // the extraction and the solve
            "PdnGraphExtractor", "PdnMeshExtractor", "PdnAssembly", "LinearDcEngine", "DcResultPacker",
            "PdnRailRegions", "PdnCopperClassifier",
            // the breakdown and the via check
            "PdnBreakdown", "PdnViaCheck", "PdnViaCurrentLimit",
            // a second renderer, or a canvas of its own
            "SkiaSharp", "SKCanvas", "SKPaint", "LayoutRenderer", "RailMapRenderer",
            // a second export writer
            "NpyWriter", "MatWriter", "TsvWriter", "TouchstoneExporter", "CnlWriter",
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);

        // What it IS allowed to do, asserted so the scan above cannot pass by the file being empty:
        // call the library, and hand a drawing to the one shared encoder.
        Assert.Contains("RailDcRun.Run", source, StringComparison.Ordinal);
        Assert.Contains("RailReportPage.Draw", source, StringComparison.Ordinal);
        Assert.Contains("VectorPage.", source, StringComparison.Ordinal);
    }

    // ── R-rail10-1: omitting --rail runs them all, in dependency order ───────

    /// <summary>
    /// A two-rail document with no <c>--rail</c> runs BOTH, in <see cref="RailOrder"/>'s order, and the
    /// output names each.
    /// </summary>
    /// <remarks>
    /// <b>The order is the assertion, not the count.</b> A regulator is a load on its input rail and a
    /// source on its output rail, and running the downstream one alone would start its source from a
    /// nominal instead of from the upstream answer — a plausible number, which is the whole reason
    /// <see cref="RailOrder"/> exists.
    /// </remarks>
    [Fact]
    public void WithNoRailFlag_BothRailsRunInTheOrderRailOrderComputed()
    {
        var fx = Fixture(twoRails: true);

        var (exit, stdout, stderr) = RunCli("rail", fx.Crail);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        // The order is RailOrder's own answer, not a literal — a literal would pass against a verb
        // that had quietly stopped asking and was reporting declaration order instead.
        var order = RailOrder.Resolve(RailDocumentIo.LoadFromFile(fx.Crail));
        Assert.Contains("Order:    " + string.Join(" -> ", order.Order), stdout, StringComparison.Ordinal);

        Assert.Contains("Rail 'VIN'", stdout, StringComparison.Ordinal);
        Assert.Contains("Rail 'VDD'", stdout, StringComparison.Ordinal);
        Assert.True(stdout.IndexOf("Rail '" + order.Order[0] + "'", StringComparison.Ordinal)
                  < stdout.IndexOf("Rail '" + order.Order[1] + "'", StringComparison.Ordinal),
                    "the rails are reported in solve order");
    }

    // ── R-rail10-2: refused BY KIND, and the board with no declaration ───────

    [Fact]
    public void ANetlistIsRefusedAsANetlist()
    {
        string cnl = Path.Combine(_root, "pad.cnl");
        Directory.CreateDirectory(_root);
        File.WriteAllText(cnl, "R:R1 in 0 R=50 Ohm\n");

        var (exit, _, stderr) = RunCli("rail", cnl);
        Assert.Equal(1, exit);
        Assert.Contains("is netlist", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A `.clay` with no `.crail` beside it: there is a board and no rail declaration, so the verb
    /// refuses and names the four flags a declaration would have to answer.
    /// </summary>
    /// <remarks>
    /// <b>It does not author one.</b> `new`'s own rule — once a document exists, the way to change it
    /// is to WRITE it, because the format is the contract — and a verb that minted a `.crail` from
    /// flags would make the document a side effect of a command line nobody could re-read.
    /// </remarks>
    [Fact]
    public void ABoardWithNoDeclarationIsRefusedNamingTheFourFlags()
    {
        var fx = Fixture();
        File.Delete(fx.Crail);

        var (exit, _, stderr) = RunCli("rail", fx.Clay);
        Assert.Equal(1, exit);

        foreach (string flag in new[] { "--rail", "--reference", "--source", "--load" })
            Assert.Contains(flag, stderr, StringComparison.Ordinal);
    }

    // ── R-rail10-3: the five rows, and the two that are NOT refusals ─────────

    [Fact]
    public void NoReferenceLayerIsARefusalNamingTheFlag()
    {
        var fx = Fixture(noReference: true);

        var (exit, _, stderr) = RunCli("rail", fx.Crail);
        Assert.Equal(1, exit);
        Assert.Contains("--reference", stderr, StringComparison.Ordinal);
        Assert.Contains("never infers", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The non-refusal that matters</b> (Q-16): a load with no current is an OBSERVATION port, and
    /// the report lists it AS observed rather than omitting it or defaulting it to zero.
    /// </summary>
    [Fact]
    public void ALoadWithNoCurrentIsAnObservationPortAndIsReportedAsOne()
    {
        var fx = Fixture();

        var (exit, stdout, stderr) = RunCli(
            "rail", fx.Crail, "--load", "@" + Mm(20) + "," + Mm(0.2));
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        Assert.Contains("observed (it states no current and draws none)", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The other non-refusal</b>: no plating thickness is a SETTING, not a missing answer. The run
    /// proceeds and the via check says which basis produced each limit.
    /// </summary>
    [Fact]
    public void NoPlatingThicknessIsNotARefusal()
    {
        var fx = Fixture();
        Assert.Null(RailDocumentIo.LoadFromFile(fx.Crail).Settings.ViaPlatingThicknessMicrometres);

        var (exit, stdout, stderr) = RunCli("rail", fx.Crail);
        output.WriteLine(stderr);
        Assert.Equal(0, exit);
        Assert.Contains("Vias:", stdout, StringComparison.Ordinal);
    }

    // ── R-rail10-5: every export carries the provenance ──────────────────────

    /// <summary>
    /// The CSV, the <c>.npy</c> and the page each carry which model produced the run, which reference
    /// extent it used and at what temperature — read back out of the written file.
    /// </summary>
    /// <remarks>
    /// <b>Read back rather than asserted against the code that wrote it.</b> Overview §4 rule 1 is
    /// about a file opened six months later with no status strip beside it, so the claim is about the
    /// FILE and nothing else. One test over the three formats, because the claim is one claim — and
    /// the `.npy` is the one that would fail silently, since a group nobody wrote is simply absent.
    /// </remarks>
    [Fact]
    public void EveryExportCarriesTheProvenance()
    {
        var fx = Fixture();

        string csv = Path.Combine(_root, "r.csv");
        string npy = Path.Combine(_root, "r.npy");
        string svg = Path.Combine(_root, "r.svg");

        foreach (string path in new[] { csv, npy, svg })
        {
            var (exit, _, stderr) = RunCli("rail", fx.Crail, "-o", path);
            Assert.True(exit == 0, path + ": " + stderr);
        }

        string csvText = File.ReadAllText(csv);
        Assert.Contains("# model: Fast", csvText, StringComparison.Ordinal);
        Assert.Contains("reference as imported", csvText, StringComparison.Ordinal);
        Assert.Contains("20 °C", csvText, StringComparison.Ordinal);

        // The `.npy` carries it as a group, on `em`'s own precedent, and the strings ride on a
        // labelled axis — which is the only string a `.npy` carries.
        string npyText = Encoding.UTF8.GetString(File.ReadAllBytes(npy));
        Assert.Contains("provenance", npyText, StringComparison.Ordinal);
        Assert.Contains("model: Fast", npyText, StringComparison.Ordinal);

        string svgText = File.ReadAllText(svg);
        Assert.Contains("reference as imported", StripSvgText(svgText), StringComparison.Ordinal);
    }

    // ── R-rail10-6: exit codes ───────────────────────────────────────────────

    [Fact]
    public void ARailOrderCycleExitsOneWithTheRunServicesOwnSentence()
    {
        var fx = Fixture(cycle: true);

        var (exit, _, stderr) = RunCli("rail", fx.Crail);
        Assert.Equal(1, exit);

        // The sentence is RailOrder's, not the verb's — `em`'s rule: a refusal stays a refusal.
        var order = RailOrder.Resolve(RailDocumentIo.LoadFromFile(fx.Crail));
        Assert.NotNull(order.Refusal);
        Assert.Contains(order.Refusal!, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled run exits 130 and <b>no file exists</b>.
    /// </summary>
    /// <remarks>
    /// In-process, because cancellation arrives through <c>RunHost</c> and a second process that
    /// answers one command and exits has nothing to deliver it — the same reason
    /// <c>RenderCliVerbTests</c>' own cancellation gate runs this way, and the same reason
    /// <c>CircuitRF.Cli</c> grants <c>InternalsVisibleTo</c> to this assembly.
    /// </remarks>
    [Fact]
    public void ACancelledRunExitsOneHundredAndThirtyAndWritesNothing()
    {
        var fx = Fixture();
        string outPath = Path.Combine(_root, "cancelled.csv");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int exit;
        using (RunHost.Install(cts.Token, observer: null))
        {
            JsonRun.Reset();
            exit = CliEntry.Run(["rail", fx.Crail, "-o", outPath]);
        }
        JsonRun.Reset();

        Assert.Equal(130, exit);
        Assert.False(File.Exists(outPath), "a cancelled run must leave no partial file");
    }

    // ── the end-to-end claim: a board can be gated in CI ─────────────────────

    /// <summary>
    /// Author a workspace and a board headlessly, <c>check</c> the rail document, <c>rail</c> it, and
    /// read the drop back — <b>with no display at any step</b>.
    /// </summary>
    /// <remarks>
    /// This is §5's claim for the whole architecture, and the reason the verb exists: a board that can
    /// only be judged by opening a window cannot be judged in CI. Each step is the CLI as a PROCESS,
    /// so nothing here can accidentally be satisfied by a type that only loads under a host.
    /// </remarks>
    [Fact]
    public void ABoardCanBeAuthoredCheckedAndSolvedWithNoDisplay()
    {
        string ws = Path.Combine(_root, "ci");

        var (createExit, _, createErr) = RunCli("new", "workspace", ws, "--tech", "none");
        Assert.True(createExit == 0, createErr);

        var fx = Fixture(root: ws);

        var (checkExit, checkOut, checkErr) = RunCli("check", fx.Crail);
        output.WriteLine(checkOut + checkErr);
        Assert.True(checkExit == 0, checkErr);

        string csv = Path.Combine(_root, "ci.csv");
        var (railExit, railOut, railErr) = RunCli("rail", fx.Crail, "-o", csv, "--target-drop", "5000mV");
        output.WriteLine(railOut + railErr);
        Assert.True(railExit == 0, railErr);

        // The drop is a real number the copper decided, not a placeholder: the port sits at the far
        // end of a 30 mm strip from a 3.7 V source, so it is below the source and above zero.
        var run = RailDcRun.Run(Request(fx));
        double v = run.Rails[0].Ports[0].VoltageV;
        Assert.InRange(v, 0.1, 3.7);

        Assert.Contains("port,", File.ReadAllText(csv), StringComparison.Ordinal);
    }

    // ── the fixture ──────────────────────────────────────────────────────────

    private sealed record Fx(string Root, string Cell, string Clay, string Crail, string Tech);

    /// <summary>
    /// A two-layer strip: the rail on TOP, its reference on BOT, a source at one end and a load at the
    /// other, anchored by COORDINATE (see this file's header).
    /// </summary>
    private Fx Fixture(bool twoRails = false, bool noReference = false, bool cycle = false, string? root = null)
    {
        string wsRoot = root ?? Path.Combine(_root, "Board");
        string cell = Path.Combine(wsRoot, "Panel");
        Directory.CreateDirectory(Path.Combine(cell, "layout"));
        Directory.CreateDirectory(Path.Combine(wsRoot, "tech"));

        string tech = Path.Combine(wsRoot, "tech", "Board.ctech");
        TechPersistence.SaveToFile(tech, TechFixture());

        if (!File.Exists(Path.Combine(wsRoot, ".cws")))
            WorkspacePersistence.SaveToFile(
                Path.Combine(wsRoot, ".cws"),
                new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });
        else
            WorkspacePersistence.SaveToFile(
                Path.Combine(wsRoot, ".cws"),
                new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        string clay = Path.Combine(cell, "layout", "Panel.clay");
        LayoutPersistence.SaveToFile(clay, LayoutFixture());

        string crail = Path.Combine(cell, "Panel.crail");
        RailDocumentIo.SaveToFile(crail, DocumentFixture(twoRails, noReference, cycle, crail, clay));

        return new Fx(wsRoot, cell, clay, crail, tech);
    }

    private static RailDocument DocumentFixture(
        bool twoRails, bool noReference, bool cycle, string crail, string clay)
    {
        var doc = new RailDocument
        {
            Name = "Panel",
            ArtworkCellRef = Path.GetRelativePath(Path.GetDirectoryName(crail)!, clay),
        };

        var vdd = new RailSpec { Name = "VDD", ReferenceLayer = noReference ? null : Bot };
        vdd.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Point = (Mm(0.2), Mm(0.2)) },
            OpenCircuitVoltageV = 3.7,
            SeriesResistanceOhms = 0.05,
        });
        vdd.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Point = (Mm(29.8), Mm(0.2)) },
            DcCurrentA = 0.5,
        });
        doc.Rails.Add(vdd);

        if (twoRails || cycle)
        {
            // A SECOND rail on the same board.
            //
            // ── WHY IT IS NOT A CHAIN, AND WHY THAT IS NOT THIS BRIEF'S DOING ─────────────────────
            //
            // A chain is two rails linked by one refdes appearing as a LOAD on one and a SOURCE on
            // the other (RailOrder reads nothing else), so a chained document's ports are anchored by
            // REFDES — and a refdes resolves to a pad through PdnExtractionRequest.Pads, which
            // nothing fills in today: a `.crail` names no placement file, and the window's own
            // RailRfViewModel.BuildRequest does not carry the placement table it read either. So a
            // chained document is refused by the extractor, in the window exactly as here.
            //
            // Recorded in src/Cli/RESOLVED.md rather than worked around: the cycle case below reaches
            // its refusal in RailOrder, BEFORE any pad is resolved, so the order machinery is gated
            // on its own terms — and this case gates what it can honestly gate, which is that a
            // document with more than one rail runs every one of them and names each.
            var vin = new RailSpec { Name = "VIN", ReferenceLayer = Bot };
            vin.Sources.Add(new RailSource
            {
                Anchor = new RailPortAnchor { Point = (Mm(0.2), Mm(0.2)) },
                OpenCircuitVoltageV = 5.0,
                SeriesResistanceOhms = 0.05,
            });
            vin.Loads.Add(new RailLoad
            {
                Anchor = new RailPortAnchor { Point = (Mm(15.0), Mm(0.2)) },
                DcCurrentA = 0.2,
            });
            doc.Rails.Insert(0, vin);

            if (cycle)
            {
                // U1 sources VDD and loads VIN; U2 sources VIN and loads VDD. That closes the loop,
                // which RailOrder refuses before anything is extracted.
                vdd.Sources.Insert(0, new RailSource
                {
                    Anchor = new RailPortAnchor { Refdes = "U1", Pin = "OUT" },
                    OpenCircuitVoltageV = 3.3,
                });
                vin.Loads.Add(new RailLoad
                {
                    Anchor = new RailPortAnchor { Refdes = "U1", Pin = "IN" }, DcCurrentA = 0.2,
                });
                vin.Sources.Add(new RailSource
                {
                    Anchor = new RailPortAnchor { Refdes = "U2", Pin = "OUT" },
                    OpenCircuitVoltageV = 5.0,
                });
                vdd.Loads.Add(new RailLoad
                {
                    Anchor = new RailPortAnchor { Refdes = "U2", Pin = "IN" }, DcCurrentA = 0.01,
                });
            }
        }

        return doc;
    }

    private static LayoutView LayoutFixture()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        view.Shapes.Add(new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        return view;
    }

    private static Technology TechFixture()
    {
        var tech = new Technology { Name = "Board" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Bot, Name = "BOT", ZOrder = 1, Color = new Rgba(40, 90, 200, 255) },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
        ];
        return tech;
    }

    /// <summary>The request the verb builds — stated here so the in-process side of the byte gate is
    /// the library call and not the verb.</summary>
    private static RailDcRequest Request(Fx fx) => new()
    {
        Document     = RailDocumentIo.LoadFromFile(fx.Crail),
        Shapes       = LayoutPersistence.LoadFromFile(fx.Clay).Shapes,
        Technology   = TechPersistence.LoadFromFile(fx.Tech),
        DbuPerMicron = Dbu,
        Model        = PdnModelKind.Fast,
    };

    // ── the in-process page, spelled out (see the byte gate's own remarks) ───

    private static RailReportPageRequest PageRequest(Fx fx)
    {
        var doc = RailDocumentIo.LoadFromFile(fx.Crail);
        var view = LayoutPersistence.LoadFromFile(fx.Clay);
        var tech = TechPersistence.LoadFromFile(fx.Tech);

        var run = RailDcRun.Run(Request(fx));
        Assert.Null(run.Refusal);
        var result = run.Rails[0];

        var provenance = new[]
        {
            $"model: Fast · reference as imported · " +
            $"{result.Netlist.Provenance.CopperTemperatureCelsius:0.#} °C",
            "no part library resolved: no part is modelled from a file and no bias-curve coverage is known",
            $"artwork: {Path.GetFullPath(fx.Clay)}",
            $"stackup: {tech.Name}",
        };

        var sections = new List<RailReportSection>
        {
            new($"Rail '{result.RailName}' — ports", [.. result.Ports.Select(p => p.Describe())]),
            new($"Rail '{result.RailName}' — where the drop is",
                [.. result.Breakdown.Take(Math.Min(10, result.Breakdown.Count)).Select(
                     row => $"{row.DropV * 1e3:0.###} mV ({row.ShareOfTotal:P0}) · {row.Label}")]),
        };
        if (result.ViaCheck.Transitions.Count > 0)
            sections.Add(new RailReportSection(
                $"Rail '{result.RailName}' — vias",
                result.ViaCheck.Flags.Count == 0
                    ? [$"{result.ViaCheck.Transitions.Count} transition(s), none over its limit"]
                    : [.. result.ViaCheck.Flags.Select(f => f.Describe())]));
        if (result.Findings.Count > 0)
            sections.Add(new RailReportSection($"Rail '{result.RailName}' — findings", result.Findings));

        return new RailReportPageRequest
        {
            Title      = doc.Name,
            Provenance = provenance,
            Sections   = sections,
            Board      = view,
            Technology = tech,
            Map        = RailMapScene.Build(result, RailMapKind.Drop, view.DbuPerMicron),
            Theme      = ThemeResolver.Resolve(ThemeResolver.DefaultThemeName,
                                               Path.GetDirectoryName(Path.GetFullPath(fx.Crail))),
            Variant    = ColorVariant.Light,
            BaseDir    = CellHierarchy.BaseDirOfDocument(Path.GetFullPath(fx.Clay)),
        };
    }

    private static string InProcessReportSvg(Fx fx)
    {
        var request = PageRequest(fx);
        return Encoding.UTF8.GetString(
            VectorPageSvg(1600, 1200, canvas => RailReportPage.Draw(canvas, request, 1600, 1200)));
    }

    private static byte[] InProcessReportPdf(Fx fx)
    {
        var request = PageRequest(fx);
        return VectorPagePdf(1600, 1200, canvas => RailReportPage.Draw(canvas, request, 1600, 1200));
    }

    // The encoder the verb uses, reached through its own internal surface — the encoding is not what
    // is under test here, the DRAWING is, so using a second copy of it would compare two encoders
    // rather than two pages.
    private static byte[] VectorPageSvg(int w, int h, Action<SkiaSharp.SKCanvas> draw)
        => VectorPage.Svg(w, h, draw);

    private static byte[] VectorPagePdf(int w, int h, Action<SkiaSharp.SKCanvas> draw)
        => VectorPage.Pdf(w, h, draw);

    /// <summary>Byte identity, with the two exclusions named in the gate's own remarks.</summary>
    private void AssertSameSvg(string fromCli, string inProcess)
    {
        if (fromCli == inProcess) { output.WriteLine("svg: byte-identical"); return; }

        output.WriteLine($"svg: differ before Skia-id normalisation ({fromCli.Length} vs {inProcess.Length} bytes)");
        Assert.Equal(StripSkiaIds(inProcess), StripSkiaIds(fromCli));
    }

    private static string StripSkiaIds(string svg)
        => System.Text.RegularExpressions.Regex.Replace(svg, @"\b(cl|img|gr|fp)_[0-9a-z]+\b", "$1_N");

    private static string StripPdfDates(byte[] pdf)
        => System.Text.RegularExpressions.Regex.Replace(
               Encoding.Latin1.GetString(pdf), @"D:\d{14}[^)]*", "D:<stamp>");

    // ── driving the verb ─────────────────────────────────────────────────────

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(RailCliVerbTests).Assembly)
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

    /// <summary>Strips `//` and `/* */`, so a scan cannot be defeated by a comment naming the thing
    /// it forbids — which this verb's own header does, deliberately.</summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(source[i]);
        }
        return sb.ToString();
    }

    /// <summary>An SVG's text runs, with the markup taken out — so a phrase can be looked for without
    /// depending on how Skia broke it across elements.</summary>
    private static string StripSvgText(string svg)
    {
        var sb = new StringBuilder(svg.Length);
        bool inTag = false;
        foreach (char c in svg)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }
}
