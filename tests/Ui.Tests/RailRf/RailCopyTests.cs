// ================================================================
//  RailCopyTests.cs — brief-railrf-9-copy.md §5; railrf.md §7, §11.7
//
//  ── THE GATE IS THE RENDERED BYTES, NOT THE WIRING ──────────────────────────────────────────
//
//  §7, and it is specific about its oracle:
//
//      A copy taken with the drop map showing MUST CONTAIN THE DROP MAP, asserted against the real
//      SVG text the way LayoutClipboardVisibilityTests and the TryRenderToSvg font tests already
//      assert against Skia's own output.
//
//  It exists because the overlay set a copy draws is an EXPLICIT PARAMETER LIST (§11.7): the EM
//  mesh, the current-density map and the DRC markers were each added to that list as they arrived,
//  and railRF's map is the fourth. The failure mode follows from the shape of that list —
//
//      an overlay nobody added to the list is silently absent from the copy. The picture is still
//      produced, it still looks correct, and the one thing the user copied it for is missing.
//
//  — so every test below that claims the map is in the picture also renders the SAME context with
//  railRF's entry REMOVED and asserts the claim fails there. Without that half, the test passes on
//  a picture that happens to contain copper.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailCopyTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;   // 1000 DBU/µm
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ══ R-rail9-3 — the map is in the picture, and the negative that gives that teeth ═════════

    /// <summary>
    /// The drop map's own marks are in the exported SVG — the legend's caption and the source and
    /// load callouts, none of which any amount of copper could produce.
    /// </summary>
    /// <remarks>
    /// The second half is the gate: the identical context with <c>railMap</c> removed from the
    /// overlay parameter list — which is precisely the state "nobody added it" leaves behind —
    /// renders a picture that still looks like a board and contains none of them.
    /// </remarks>
    [Fact]
    public void ACopyTakenWithTheDropMapShowing_ContainsTheDropMap()
    {
        var result = SolvedRail();
        var scene  = RailMapScene.Build(result, RailMapKind.Drop, Dbu);

        string withMap    = SvgOf(ContextFor(scene));
        string withoutMap = SvgOf(ContextFor(railMap: null));

        // The legend carries the rail's name and the model that produced the numbers (§2.9 rule 1 —
        // a picture pasted into a document is a result that has left the window behind).
        Assert.Contains("VDD", withMap, StringComparison.Ordinal);
        Assert.Contains("Fast model", withMap, StringComparison.Ordinal);

        // The source and the load, called out on the copper they resolved to.
        Assert.Contains("BT1", withMap, StringComparison.Ordinal);

        // …and with railRF's entry removed from the list, not one of them is there. This is the half
        // that makes the assertions above mean something: the picture is still produced.
        Assert.True(withoutMap.Length > 0, "the copper-only picture must still render");
        Assert.DoesNotContain("Fast model", withoutMap, StringComparison.Ordinal);
        Assert.DoesNotContain("BT1", withoutMap, StringComparison.Ordinal);
    }

    // ══ R-rail9-2 — the page is framed on the PAINTED extent, overlay included ════════════════

    /// <summary>
    /// The legend sits BELOW the copper, outside its bbox, and the page contains it.
    /// </summary>
    /// <remarks>
    /// The same requirement as brief 8's R-rail8-7 seen from the other side, and it is why the
    /// legend's placement is a correctness question rather than a cosmetic one: a drop map is
    /// co-extensive with the copper, so a page framed on geometry alone looks right until a legend,
    /// a source marker or a flagged-via callout lands outside it.
    /// </remarks>
    [Fact]
    public void ThePageIsFramedOnThePaintedExtent_IncludingTheLegendOutsideTheCopper()
    {
        var scene = RailMapScene.Build(SolvedRail(), RailMapKind.Drop, Dbu);
        var legend = scene.Legend;
        Assert.NotNull(legend);

        // The fixture's own premise: the plate really is outside the copper, which starts at y = 0.
        Assert.True(legend!.Box.MinY < 0,
            $"fixture no longer places the legend outside the copper (MinY {legend.Box.MinY})");

        var withMap = LayoutClipboard.SelectionBoundsForTests(ContextFor(scene));
        var without = LayoutClipboard.SelectionBoundsForTests(ContextFor(railMap: null));
        Assert.NotNull(withMap);
        Assert.NotNull(without);

        // The page reaches down to the plate…
        Assert.True(withMap!.Value.BbMinY <= legend.Box.MinY,
            $"page starts at {withMap.Value.BbMinY}; the legend reaches {legend.Box.MinY}");

        // …and would not have, with railRF's entry removed from the list.
        Assert.True(without!.Value.BbMinY > legend.Box.MinY);
        Assert.True(withMap.Value.WorldH > without.Value.WorldH);
    }

    /// <summary>
    /// And the other half of §7's sentence: <b>a hidden layer does not size the page.</b>
    /// </summary>
    /// <remarks>
    /// Owner report, 2026-09-04, on the layout copy: two shapes far apart on two layers with one
    /// hidden pasted as a mostly-empty page with the visible shape too small to read. The rule is
    /// <c>LayerDef.Visible</c>, the same flag <c>LayoutRenderer.Draw</c> gates each layer on — and a
    /// railRF copy inherits it by going through the same bounds pass rather than framing its own page.
    /// </remarks>
    [Fact]
    public void AHiddenLayerDoesNotSizeTheRailPage()
    {
        var scene = RailMapScene.Build(SolvedRail(), RailMapKind.Drop, Dbu);

        // A second piece of copper a long way off, on a layer the user has turned off.
        var board = Artwork();
        board.Shapes.Add(new RectShape { Layer = Bot, X1 = Mm(200), Y1 = 0, X2 = Mm(210), Y2 = Mm(0.4) });

        double shown  = LayoutClipboard.SelectionBoundsForTests(ContextFor(scene, board, TestBoard(botVisible: false)))!.Value.WorldW;
        double hidden = LayoutClipboard.SelectionBoundsForTests(ContextFor(scene, board, TestBoard(botVisible: true)))!.Value.WorldW;

        Assert.True(shown < Mm(40), $"the hidden layer sized the page anyway (width {shown})");
        Assert.True(hidden > Mm(200), $"the visible case must span both (width {hidden})");
    }

    // ══ R-rail9-4 — what lands on the clipboard ═══════════════════════════════════════════════

    /// <summary>
    /// The text flavour is the rail's own state, marker-guarded: it round-trips into another railRF
    /// document, and a foreign payload is <b>ignored rather than half-parsed</b>.
    /// </summary>
    [Fact]
    public void TheJsonPayloadRoundTrips_AndAForeignPayloadIsIgnored()
    {
        var source = DocumentWithOneRail();
        Assert.True(RailClipboard.TryDeserialize(RailClipboard.Serialize(source), out var pasted));

        // "Into another railRF document": the pasted state is what a second window would open on.
        var destination = new RailRfViewModel(pasted!, null);
        Assert.Equal("VDD", Assert.Single(destination.Document.Rails).Name);
        Assert.Equal(Bot, destination.Document.Rails[0].ReferenceLayer);
        Assert.Equal(3.7, destination.Document.Rails[0].Sources[0].OpenCircuitVoltageV);

        // Everything else on that one shared channel is a clean false — never an exception, never a
        // document half-replaced by something unrelated.
        foreach (string foreign in new[]
                 {
                     "",
                     "   ",
                     "a line somebody copied out of a terminal",
                     "{\"marker\":\"circuitrf/layout-clipboard-v1\",\"Shapes\":[]}",
                     RailDocumentIo.SerializeUnvalidated(source),      // a bare .crail, unwrapped
                     RailClipboard.Serialize(source)[..40],            // truncated
                 })
        {
            Assert.False(RailClipboard.TryDeserialize(foreign, out var nothing), $"accepted: {foreign}");
            Assert.Null(nothing);
        }
    }

    /// <summary>
    /// The overlays are <b>absent from the JSON and present in the picture</b> — the mesh's and the
    /// DRC markers' own contract, which railRF's map follows exactly.
    /// </summary>
    /// <remarks>
    /// A map is a RESULT, not geometry. Pasting one into another railRF document as though it were
    /// state would carry an answer computed from artwork the destination does not have.
    /// </remarks>
    [Fact]
    public void TheMapRidesThePictureAndNeverTheJson()
    {
        var result = SolvedRail();
        string json = RailClipboard.Serialize(DocumentWithOneRail());

        Assert.DoesNotContain("Fast model", json, StringComparison.Ordinal);
        Assert.DoesNotContain("legend", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tile", json, StringComparison.OrdinalIgnoreCase);

        // …and the same words are in the picture, which is where they belong.
        Assert.Contains("Fast model", SvgOf(ContextFor(RailMapScene.Build(result, RailMapKind.Drop, Dbu))),
                        StringComparison.Ordinal);
    }

    // ══ R-rail9-5 — an empty view is NOT a licence to return early ════════════════════════════

    /// <summary>
    /// A copy of an unsolved document — and of a window with no board at all — still produces every
    /// format rather than returning without touching the clipboard.
    /// </summary>
    /// <remarks>
    /// <i>A copy that writes nothing to the system clipboard leaves the PREVIOUS copy sitting there,
    /// so the next paste produces something unrelated and nothing reports a failure — that is
    /// precisely what a ruler-only layout copy did</i> (§11.7), and it is a further reason railRF's
    /// copy is of the VIEW rather than of a selection.
    ///
    /// <para><b>Three of the four are asserted here, and the fourth is stated rather than asserted.</b>
    /// The raster flavour is an <c>Avalonia.Media.Imaging.Bitmap</c>, which cannot be constructed
    /// without a platform render interface this project stands up no application for; asserting it
    /// would test Avalonia's platform stack rather than railRF. It is best-effort by contract in every
    /// graphic copy in this repo — a raster failure must never cost the vector formats — and the two
    /// vector builders below are what "always writes" actually rests on.</para>
    /// </remarks>
    [Fact]
    public void ACopyOfAnUnsolvedOrEmptyDocumentStillWritesEveryFormat()
    {
        foreach (var (what, ctx, document) in new (string, LayoutClipboard.ExportContext, RailDocument)[]
                 {
                     ("unsolved, with a board",
                      ContextFor(RailMapScene.Build(null, RailMapKind.Drop, Dbu)),
                      DocumentWithOneRail()),

                     ("no board at all, no result",
                      ContextFor(RailMapScene.Build(null, RailMapKind.Drop, Dbu), board: new LayoutView()),
                      new RailDocument()),
                 })
        {
            Assert.True(RailGraphicExport.BuildPdf(ctx).Length > 0, $"{what}: no PDF");
            Assert.Contains("<svg", RailGraphicExport.BuildSvg(ctx), StringComparison.Ordinal);
            Assert.True(RailClipboard.Serialize(document).Length > 0, $"{what}: no text payload");
        }
    }

    /// <summary>
    /// And a half-built document is copyable: the rail exists but nothing has been picked for it yet,
    /// which is exactly the state somebody copies from while they are still working.
    /// </summary>
    [Fact]
    public void AHalfBuiltDocumentIsStillCopyable()
    {
        var doc = new RailDocument();
        doc.Rails.Add(new RailSpec { Name = "" });          // refused by the FILE writer, on purpose

        Assert.NotNull(doc.Refusal());                       // fixture premise: this cannot be saved…
        Assert.True(RailClipboard.TryDeserialize(RailClipboard.Serialize(doc), out var back));
        Assert.Single(back!.Rails);                          // …and is copied anyway.
    }

    // ══ The exported picture is the --detail full one ═════════════════════════════════════════

    /// <summary>
    /// What is STORED is what is drawn: the export turns every level-of-detail tier off
    /// (<c>DetailPixelThreshold = -1</c>), so a board of small shapes emits one element per shape
    /// rather than the merged fill a canvas would draw.
    /// </summary>
    /// <remarks>
    /// A PDF or SVG page has no device pixels to budget against, and a pasted bitmap may be rescaled
    /// away from the size it was rendered at — the same reason <c>circuitrf render --detail full</c>
    /// is that verb's default. The contrast render below is what makes this a measurement rather than
    /// an assumption: at the screen tier the same content collapses into far fewer elements, so the
    /// count the export produces could not have come from an interactive render.
    /// </remarks>
    [Fact]
    public void TheExportedSvgIsTheFullDetailPicture()
    {
        const int shapes = 240;

        var board = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um };
        for (int i = 0; i < shapes; i++)
            board.Shapes.Add(new RectShape
            {
                Layer = Top,
                X1 = Mm(0.25 * i), Y1 = 0,
                X2 = Mm(0.25 * i) + Um(40), Y2 = Um(40),
            });

        string export = SvgOf(ContextFor(railMap: null, board: board));
        int exported = CountPathElements(export);

        Assert.True(exported >= shapes,
            $"the export emitted {exported} elements for {shapes} stored shapes — a tier is engaging");

        // The contrast: the same content on a canvas-sized viewport, with the tiers left alone.
        int screen = CountPathElements(ScreenDetailSvg(board));
        Assert.True(screen < shapes,
            $"the screen-detail render emitted {screen} elements for {shapes} shapes — the tier did " +
            "not engage, so the assertion above is satisfied by anything");
    }

    // ══ R-rail9-1 — railRF writes no clipboard code ═══════════════════════════════════════════

    /// <summary>
    /// <b>The rule, stated as a rule.</b> Nothing under <c>src/Ui/RailRf/</c> touches the system
    /// clipboard itself: no <c>IClipboard</c>, no data-transfer object, no <c>SetTextAsync</c>, no
    /// P/Invoke. Every write goes through <see cref="PlotExporter.SetClipboardDataAsync"/>.
    /// </summary>
    /// <remarks>
    /// The owner instruction this holds shut: <i>it must reuse the exporter that already exists —
    /// this path has cost real debugging time across three platforms and none of it should be spent
    /// again.</i> Comments are stripped first, because this very file's own header discusses every
    /// one of those names without using any of them.
    /// </remarks>
    [Fact]
    public void RailRfWritesNoClipboardCode()
    {
        string[] banned =
        [
            "IClipboard", "IDataObject", "DataTransfer", "DataFormat.", "ClipboardFormats",
            "WindowsClipboard", "SetTextAsync", "SetDataAsync", "TryGetTextAsync",
            "DllImport", "LibraryImport",
        ];

        var offenders = new List<string>();
        int writes = 0;

        foreach (string path in Directory.EnumerateFiles(RailRfRoot(), "*.cs", SearchOption.AllDirectories))
        {
            string code = StripComments(File.ReadAllText(path));
            string name = Path.GetFileName(path);

            foreach (string token in banned)
                if (code.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{name}: {token}");

            // And every WRITE to the system clipboard is the one sanctioned call. Counted rather than
            // merely permitted: "one call and not four" is itself the contract — a second Avalonia
            // clipboard session fails on Windows, for reasons WindowsClipboard's own header records.
            foreach (System.Text.RegularExpressions.Match m in
                     Regex.Matches(code, @"([\w\.]*)SetClipboardDataAsync"))
            {
                if (m.Groups[1].Value != "PlotExporter.") offenders.Add($"{name}: {m.Value}");
                else writes++;
            }
        }

        Assert.True(offenders.Count == 0,
            "railRF writes no clipboard code (R-rail9-1); found:\n  " + string.Join("\n  ", offenders));
        Assert.Equal(1, writes);
    }

    // ══ fixtures ══════════════════════════════════════════════════════════════════════════════

    private static LayoutView Artwork()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        view.Shapes.Add(new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        return view;
    }

    /// <summary>The real export context, through the seam <c>RailGraphicExport</c> itself uses.</summary>
    private static LayoutClipboard.ExportContext ContextFor(
        RailMapScene? railMap, LayoutView? board = null, Technology? tech = null)
        => LayoutClipboard.MakeExportContext(
            RailGraphicExport.PayloadOf(board ?? Artwork()),
            tech ?? TestBoard(botVisible: true),
            LayoutRenderTheme.Light,
            transparent: true,
            baseDir: "",
            railMap: railMap,
            railTheme: RailMapTheme.Light);

    private static string SvgOf(LayoutClipboard.ExportContext ctx)
    {
        var svg = LayoutClipboard.TryRenderToSvg(ctx);
        Assert.NotNull(svg);
        return svg!.Value.Svg;
    }

    /// <summary>The same view on a canvas-sized viewport with the detail tiers left engaged.</summary>
    private static string ScreenDetailSvg(LayoutView board)
    {
        using var stream = new SkiaSharp.SKDynamicMemoryWStream();
        var vp = new LayoutViewport(0, 0, 800.0 / (board.Shapes.Count * 250_000.0), 800, 200);
        using (var canvas = SkiaSharp.SKSvgCanvas.Create(new SkiaSharp.SKRect(0, 0, 800, 200), stream))
            LayoutRenderer.Draw(canvas, board, TestBoard(botVisible: true), vp,
                                new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false });
        return System.Text.Encoding.UTF8.GetString(stream.DetachAsData().ToArray());
    }

    private static int CountPathElements(string svg) => Regex.Matches(svg, "<path").Count;

    private static RailDocument DocumentWithOneRail()
    {
        var doc = new RailDocument { Name = "copy test" };
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7,
            SeriesResistanceOhms = 0.2,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" },
            DcCurrentA = 0.12,
        });
        doc.Rails.Add(rail);
        return doc;
    }

    /// <summary>One straight supply trace over a reference strip, solved by <c>RailDcRun</c> itself —
    /// so the scene is built from a real extraction rather than from a hand-made result that could not
    /// have come out of one.</summary>
    private static RailDcResult SolvedRail()
    {
        var run = RailDcRun.Run(new RailDcRequest
        {
            Document     = DocumentWithOneRail(),
            Technology   = TestBoard(botVisible: true),
            DbuPerMicron = Dbu,
            Shapes =
            [
                new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) },
                new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) },
            ],
            Pads =
            [
                new PdnPad("BT1", "1", "VDD", Mm(0.2), Mm(0.2)),
                new PdnPad("U1", "VDD", "VDD", Mm(29.8), Mm(0.2)),
            ],
        });

        Assert.Null(run.Refusal);
        return run.Rails[0];
    }

    private static Technology TestBoard(bool botVisible)
    {
        var tech = new Technology { Name = "test board" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", Color = new Design.Theming.Rgba(200, 120, 0), Visible = true },
            new LayerDef { Key = Bot, Name = "BOT", Color = new Design.Theming.Rgba(0, 120, 200), Visible = botVisible },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
        ];
        return tech;
    }

    private static string RailRfRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Ui", "RailRf")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Ui", "RailRf");
    }

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join('\n', code.Split('\n').Select(l =>
        {
            int i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l[..i] : l;
        }));
    }
}
