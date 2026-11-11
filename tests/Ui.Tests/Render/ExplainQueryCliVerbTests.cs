// ================================================================
//  ExplainQueryCliVerbTests.cs — RND-3's gates (brief-render-3-query-surface.md §5) for
//  `circuitrf explain --cells`, `--layers` and `--extents`.
//
//  ── What these tests are actually pinning ─────────────────────────────────────────────────────
//
//  Every one of the three claims to REPORT something another part of circuitRF already decides,
//  rather than to decide it here. A test that asserted the reported numbers against transcriptions
//  of those numbers would be just as green on the day the two parted company, so each gate is an
//  AGREEMENT gate against the other path:
//
//    --cells   vs `check`'s own folder walk (gate 1) and vs CellFolder.ResolvePrimary's five
//              states (gate 2).
//    --layers  vs `render --json`'s own layers[].shapes (gate 4) — the two now come from one walk
//              in CellHierarchy, so a disagreement means R-rnd3-6's "do not write a second walk"
//              was violated.
//    --extents vs `render --fit --json`'s extents (gate 5), field for field, for all three
//              document kinds — and vs CellHierarchy.InstanceBbox for the hierarchy case (gate 6).
//
//  Every process launch is the already-built CircuitRF.Cli.dll rather than `dotnet run --project`,
//  which is a hang inside `dotnet test` and not merely a cost (EmCliVerbTests.RunCli records why).
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using Xunit.Abstractions;
using Symbol = CircuitRF.Design.Symbol.Symbol;

namespace CircuitRF.Ui.Tests.Render;

public sealed class ExplainQueryCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-explainq-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey Metal1 = new(1, 0);
    private static readonly LayerKey Metal2 = new(2, 0);
    private static readonly LayerKey Silk   = new(5, 0);

    // ══ gate 1 — --cells finds every cell `check` walks ═══════════════════════════════════════════

    /// <summary>
    /// The two sets are COMPARED, not eyeballed: a cell that <c>check</c> validates and <c>--cells</c>
    /// does not list is a bug in one of them, and this says which.
    ///
    /// <para><b>Against <c>--all</c>, and that is the honest comparison.</b> <c>check</c>'s folder walk
    /// descends into <c>.generated-cells</c> — it is a folder like any other to a validator, and a
    /// generated cell holding a defect is a defect — while <c>--cells</c> hides it by default for the
    /// reason the project tree hides it (R-rnd3-4). The gate below pins that difference separately; if
    /// this one compared against the default it would be asserting the exclusion twice and the
    /// AGREEMENT not at all.</para>
    /// </summary>
    [Fact]
    public void Cells_ListsEveryCell_CheckAlsoWalks()
    {
        // The AWKWARD workspace on purpose — four cells whose primacy resolves five different ways,
        // one of them nested a folder deep and one under the reserved generated-cells folder. A
        // one-cell fixture would agree with anything.
        string root = BuildAwkwardWorkspace();

        var listed = CellFolders(ExplainJson(root, "--cells", "--all"));

        // `check`'s own walk, read out of its per-document report: every document it opened under a
        // cell folder names that folder.
        var checkedDirs = CheckedCellFolders(root);

        output.WriteLine("--cells --all: " + string.Join("\n               ", listed));
        output.WriteLine("check:         " + string.Join("\n               ", checkedDirs));

        // The vacuity guard: an empty set on either side satisfies both assertions below and proves
        // nothing. The fixture holds six cells, one of them a folder deep and one under the reserved
        // generated-cells folder.
        Assert.Equal(6, listed.Length);
        // `check` walks no hidden folder (a cache or a history, walked only when named), and the generated-cells store is
        // one: its walk is every listed cell but that one.
        string generated = Path.DirectorySeparatorChar + ".generated-cells" + Path.DirectorySeparatorChar;
        Assert.Equal(5, checkedDirs.Length);
        Assert.Equal(listed.Where(d => !d.Contains(generated, StringComparison.Ordinal)).Order(StringComparer.Ordinal),
                     checkedDirs.Order(StringComparer.Ordinal));
    }

    // ══ gate 2 — the awkward cells are listed WITH their state ════════════════════════════════════

    /// <summary>
    /// R-rnd3-3. Four fixtures, one per interesting branch of <c>CellFolder.ResolvePrimary</c> plus a
    /// defective file, each of which <c>--cells</c> must LIST rather than omit or silently resolve.
    ///
    /// <para><b>The states are compared against <c>check</c>'s own findings for the same folders</b>,
    /// not against a transcription: a missing named primary is <c>check.cell.primary-missing</c> and an
    /// ambiguous folder is <c>check.cell.no-primary</c>, so if the two verbs ever disagreed about a
    /// cell the pair of assertions here fails rather than one of them quietly changing.</para>
    /// </summary>
    [Fact]
    public void Cells_ReportsTheResolution_IncludingItsFailureModes()
    {
        var ws = BuildAwkwardWorkspace();

        var doc = ExplainJson(ws, "--cells");
        var byName = Cells(doc).ToDictionary(c => c.GetProperty("name").GetString()!, c => c);

        Assert.Equal("missing-named-primary", State(byName["BrokenPrimary"], "schematic"));
        Assert.Equal("no-primary",            State(byName["Ambiguous"],     "schematic"));
        Assert.Equal("no-view",               State(byName["SymbolOnly"],    "schematic"));
        Assert.Equal("sole-file",             State(byName["SymbolOnly"],    "symbol"));
        Assert.Equal("sole-file",             State(byName["Defective"],     "schematic"));

        // The ambiguity SAYS what it is ambiguous between — the whole value of this over `ls`.
        var candidates = View(byName["Ambiguous"], "schematic").GetProperty("candidates")
                             .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(3, candidates.Length);

        // A defective view file carries CellViewFileValidator's own sentence — the one `check` reports.
        Assert.False(string.IsNullOrEmpty(View(byName["Defective"], "schematic").GetProperty("defect").GetString()));

        // And the two verbs agree about which folders are wrong.
        var ids = CheckIds(ws);
        Assert.Contains("check.cell.primary-missing", ids);
        Assert.Contains("check.cell.no-primary",      ids);
        Assert.Contains("check.view.defect",          ids);

        static string State(JsonElement cell, string view) => View(cell, view).GetProperty("state").GetString()!;
        static JsonElement View(JsonElement cell, string view)
            => cell.GetProperty("views").EnumerateArray()
                   .Single(v => v.GetProperty("type").GetString() == view);
    }

    // ══ gate 3 — .generated-cells is excluded by default, included with --all ═════════════════════

    /// <summary>
    /// R-rnd3-4. The reserved folder holds one content-addressed cell per distinct PCell placement and
    /// the project tree hides it deliberately; a caller debugging a PCell genuinely does want to reach
    /// one, so the exclusion is a default and <c>--all</c> lifts it.
    ///
    /// <para>The folder is built here rather than by placing a real PCell — what is being pinned is
    /// the EXCLUSION RULE, which keys on the folder name and nothing else, and driving a Python
    /// generator to produce a folder whose name is a content hash would test the generator.</para>
    /// </summary>
    [Fact]
    public void Cells_HidesGeneratedCellsByDefault_AndIncludesThemWithAll()
    {
        var ws = BuildWorkspace();

        string gen = Path.Combine(ws.Root, ReservedFolders.GeneratedCells);
        string genCell = CellFolder.CreateCellFolder(gen, "MLIN_9f3c1a02");
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(genCell, ViewType.Layout), "MLIN_9f3c1a02.clay"),
            LayoutFixture());

        var plain = CellFolders(ExplainJson(ws.Root, "--cells"));
        var all   = CellFolders(ExplainJson(ws.Root, "--cells", "--all"));

        Assert.DoesNotContain(Path.GetFullPath(genCell), plain);
        Assert.Contains(Path.GetFullPath(genCell), all);

        // And it is MARKED, so a caller that asked for them can tell which is which.
        var row = Cells(ExplainJson(ws.Root, "--cells", "--all"))
                  .Single(c => c.GetProperty("name").GetString() == "MLIN_9f3c1a02");
        Assert.True(row.GetProperty("generated").GetBoolean());
    }

    /// <summary>A cell folder reports THAT cell, one entry, in the same shape — which is what makes
    /// this composable with <c>render</c>: ask what views a cell has, then render one.</summary>
    [Fact]
    public void Cells_OnACellFolder_ReportsThatOneCell()
    {
        var ws = BuildWorkspace();
        var rows = Cells(ExplainJson(ws.CellDir, "--cells"));
        Assert.Single(rows);
        Assert.Equal("Stage1", rows[0].GetProperty("name").GetString());
        // One row per view TYPE, present or not — schematic, symbol, layout and, since brief-em3d-41, 3D.
        Assert.Equal(Enum.GetValues<ViewType>().Length, rows[0].GetProperty("views").GetArrayLength());
    }

    // ══ gate 4 — --layers' shape counts agree with a render ═══════════════════════════════════════

    /// <summary>
    /// R-rnd3-6. Two paths, one answer.
    ///
    /// <para><b>Against <c>render --json</c>'s <c>layers[].shapes</c>, and NOT against its
    /// <c>counters.shapesDrawn</c>, which is what §5's gate 4 names.</b> That was measured and it
    /// cannot hold: <c>LayoutRenderResult.ShapesDrawn</c> counts the top-level shapes a frame issued a
    /// draw call for, and an instance's interior is accounted in <c>InstancesDrawn</c> instead — so on
    /// the hierarchical fixture below the renderer reports 1 where the picture draws 7. The field that
    /// answers the same question <c>explain --layers</c> answers is <c>layers[].shapes</c>, and RND-3
    /// pointed BOTH of them at <c>CellHierarchy.ShapeCountsByLayer</c> so there is one walk rather than
    /// two. See src/Cli/RESOLVED.md.</para>
    /// </summary>
    [Fact]
    public void Layers_ShapeCounts_AgreeWithWhatRenderReports()
    {
        var ws = BuildHierarchicalWorkspace();

        var fromExplain = ExplainJson(ws.Clay, "--layers")
            .GetProperty("layers").GetProperty("layers").EnumerateArray()
            .ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("shapes").GetInt64());

        var fromRender = RenderJson(ws.Clay)
            .GetProperty("layers").EnumerateArray()
            .ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("shapes").GetInt64());

        output.WriteLine("explain: " + string.Join(", ", fromExplain.Select(kv => $"{kv.Key}={kv.Value}")));
        output.WriteLine("render:  " + string.Join(", ", fromRender.Select(kv => $"{kv.Key}={kv.Value}")));

        Assert.Equal(fromRender, fromExplain);

        // And the number is HIERARCHICAL, which is the property the agreement alone cannot show: the
        // top cell draws one rectangle on Metal1 and places a 2x3 array of a cell that draws one more.
        Assert.Equal(7, fromExplain["Metal1"]);
        Assert.Equal(6, fromExplain["Metal2"]);
        Assert.Equal(0, fromExplain["Silk"]);
    }

    /// <summary>
    /// The same agreement over the case the fixture above cannot reach: a document drawing on a layer
    /// key the technology does NOT define.
    ///
    /// <para>That layer renders — on the generated fallback palette, which is what makes it a layer as
    /// far as the picture is concerned. <c>explain --layers</c> listed it and <c>render --json</c> did
    /// not, so the two verbs this series deliberately pointed at ONE walk disagreed about which layers
    /// the document has, on exactly the documents where the state is ordinary: imports
    /// (<c>layout-view.md</c> §2.4). The set comparison is the assertion; the vacuity guard is that
    /// the generated name is in it.</para>
    /// </summary>
    [Fact]
    public void Layers_ThatTheTechnologyDoesNotDefine_AreReportedByBothVerbs()
    {
        var ws = BuildWorkspace("Undefined");
        var view = LayoutFixture();
        view.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(99, 0), X1 = 800 * Dbu, Y1 = 0, X2 = 900 * Dbu, Y2 = 100 * Dbu,
        });
        LayoutPersistence.SaveToFile(ws.Clay, view);

        var fromExplain = ExplainJson(ws.Clay, "--layers")
            .GetProperty("layers").GetProperty("layers").EnumerateArray()
            .ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("shapes").GetInt64());

        var fromRender = RenderJson(ws.Clay)
            .GetProperty("layers").EnumerateArray()
            .ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("shapes").GetInt64());

        Assert.Equal(1, fromExplain["L99/0"]);      // vacuity guard: the case is actually present
        Assert.Equal(fromRender, fromExplain);
    }

    /// <summary>R-rnd3-5. On a <c>.ctech</c> alone there is no document to count against, so
    /// <c>shapes</c> is ABSENT — not zero, which would be a claim about a document that is not
    /// there.</summary>
    [Fact]
    public void Layers_OnATechnologyAlone_OmitsTheShapeCount()
    {
        var ws = BuildWorkspace();
        var layers = ExplainJson(ws.Tech, "--layers").GetProperty("layers");

        foreach (var l in layers.GetProperty("layers").EnumerateArray())
        {
            Assert.False(l.TryGetProperty("shapes", out _));
            Assert.False(l.TryGetProperty("instancesUsing", out _));
        }
        Assert.Equal(3, layers.GetProperty("layers").GetArrayLength());
    }

    /// <summary>R-rnd3-7. Where the technology walk resolves to nothing, that IS the answer — with the
    /// fallback palette NAMED, since that is what <c>render</c> will actually draw with and a caller
    /// needs to know the colours it gets are not the process's.</summary>
    [Fact]
    public void Layers_WithNoTechnology_NamesTheFallbackPalette()
    {
        string loose = Path.Combine(_root, "loose");
        Directory.CreateDirectory(loose);
        string clay = Path.Combine(loose, "Orphan.clay");
        LayoutPersistence.SaveToFile(clay, LayoutFixture());

        var layers = ExplainJson(clay, "--layers").GetProperty("layers");

        Assert.False(layers.TryGetProperty("technology", out _));
        Assert.Contains("fallback palette", layers.GetProperty("resolvedBy").GetString());

        // The keys the document DRAWS on are still reported — on the generated palette, which is what
        // renders. An empty list would read as "this document uses no layers".
        var names = layers.GetProperty("layers").EnumerateArray()
                          .Select(l => l.GetProperty("name").GetString()).ToArray();
        Assert.Contains("L1/0", names);
        Assert.Contains("L2/0", names);
    }

    // ══ gate 5 — --extents equals --fit ═══════════════════════════════════════════════════════════

    /// <summary>
    /// R-rnd3-9. The two numbers come from one function in <c>CircuitRF.Render</c>, and this compares
    /// them as the caller would: <c>render --fit --json</c>'s <c>extents</c> against
    /// <c>explain --extents</c>', for a schematic, a symbol and a layout.
    ///
    /// <para><b>Compared as text, at full round-trippable precision</b>, so a difference of one ulp
    /// fails rather than passing an epsilon test — which is the point of "from the same code" as
    /// opposed to "agrees today".</para>
    /// </summary>
    [Theory]
    [InlineData("layout")]
    [InlineData("schematic")]
    [InlineData("symbol")]
    public void Extents_AreTheSameBoxRenderFitsOn(string kind)
    {
        var ws = BuildWorkspace();
        string path = kind switch
        {
            "layout"    => ws.Clay,
            "schematic" => ws.Csch,
            _           => ws.Csym,
        };

        var fromRender  = RenderJson(path, "--fit").GetProperty("extents");
        var fromExplain = ExplainJson(path, "--extents").GetProperty("extents");

        foreach (string field in new[] { "x0", "y0", "x1", "y1", "unit", "scale" })
        {
            string r = fromRender.GetProperty(field).GetRawText();
            string e = fromExplain.GetProperty(field).GetRawText();
            output.WriteLine($"{kind}.{field}: render={r} explain={e}");
            Assert.Equal(r, e);
        }

        // width/height are explain's own, and they are the box rather than a second measurement.
        Assert.Equal(fromExplain.GetProperty("x1").GetDouble() - fromExplain.GetProperty("x0").GetDouble(),
                     fromExplain.GetProperty("width").GetDouble(), 12);
    }

    // ══ AUT-12 R-aut12-3 — the output of this verb is the input of `render --window` ══════════════

    /// <summary>
    /// <b>The whole requirement, as a round trip.</b> <c>explain --extents</c> emits base-SI metres
    /// and <c>render --window</c> refuses a bare number — correctly, and for a good reason — so the
    /// one tool that says WHERE the content is emitted exactly what the other tool rejected, and
    /// every windowed render needed a hand conversion.
    ///
    /// <para>Asserted VERBATIM: the string is taken out of the document and handed to the other verb
    /// as one argument, with nothing done to it. A test that re-derived the coordinates would be
    /// testing its own arithmetic, which is the arithmetic this requirement exists to delete.</para>
    /// </summary>
    [Theory]
    [InlineData("layout")]
    [InlineData("schematic")]
    [InlineData("symbol")]
    public void TheEmittedWindowString_IsAcceptedVerbatimByRenderWindow(string kind)
    {
        var ws = BuildWorkspace();
        string path = kind switch { "layout" => ws.Clay, "schematic" => ws.Csch, _ => ws.Csym };

        string window = ExplainJson(path, "--extents").GetProperty("extents")
                            .GetProperty("window").GetString()!;
        output.WriteLine($"{kind}: --window {window}");

        string outPath = Path.Combine(_root, $"roundtrip-{kind}.png");
        var run = RunCli("render", path, "-o", outPath, "--window", window);
        Assert.True(run.ExitCode == 0, $"'{window}' was refused:\n{run.StdErr}{run.StdOut}");
        Assert.True(File.Exists(outPath));
    }

    /// <summary>
    /// The vacuity guard, and the defect in one line: the NUMERIC fields spelled straight out of the
    /// document are refused on a layout. Without this, a <c>window</c> field that happened to be
    /// unit-bearing by accident would look like a fix.
    /// </summary>
    [Fact]
    public void TheNumericExtentsSpelledDirectly_AreStillRefusedOnALayout()
    {
        var ws = BuildWorkspace();
        var box = ExplainJson(ws.Clay, "--extents").GetProperty("extents");

        string bare = string.Join(',', new[] { "x0", "y0", "x1", "y1" }
            .Select(f => box.GetProperty(f).GetDouble().ToString("R", CultureInfo.InvariantCulture)));

        var run = RunCli("render", ws.Clay, "-o", Path.Combine(_root, "never.png"),
                         "--window", bare, "--json");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("render.viewport.unit-required", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each PER-LAYER box carries the same spelling, which is what makes "frame on the copper" a
    /// paste rather than a calculation — the companion to <c>render --fit-layers</c>.
    /// </summary>
    [Fact]
    public void EveryPerLayerExtent_CarriesAWindowStringTheRenderTakes()
    {
        var ws = BuildWorkspace();
        var perLayer = ExplainJson(ws.Clay, "--extents").GetProperty("extents")
                           .GetProperty("perLayer").EnumerateArray().ToArray();
        Assert.NotEmpty(perLayer);

        foreach (var layer in perLayer)
        {
            string window = layer.GetProperty("window").GetString()!;
            output.WriteLine($"{layer.GetProperty("name").GetString()}: {window}");
            var run = RunCli("render", ws.Clay, "-o", Path.Combine(_root, "per-layer.png"),
                             "--window", window);
            Assert.True(run.ExitCode == 0, $"'{window}' was refused:\n{run.StdErr}{run.StdOut}");
        }
    }

    /// <summary>
    /// The string reads back to the SAME DBU, at two display units and two DBU resolutions — the part
    /// a "did it get accepted" test cannot see. A fixed decimal count silently quantises a
    /// nanometre-resolution layout written in millimetres, which is a window off by a hair and
    /// invisible in the picture; the count is derived from the document's own scale instead.
    /// </summary>
    [Theory]
    [InlineData(LayoutUnit.Um, 1000)]
    [InlineData(LayoutUnit.Mm, 1000)]
    [InlineData(LayoutUnit.Mil, 1000)]
    [InlineData(LayoutUnit.Nm, 1)]
    public void TheEmittedWindowString_ReadsBackToTheSameDbu(LayoutUnit unit, int dbuPerMicron)
    {
        string dir = Path.Combine(_root, $"spell-{unit}-{dbuPerMicron}");
        Directory.CreateDirectory(dir);
        string clay = Path.Combine(dir, "Spelled.clay");

        // Deliberately not round in ANY unit: a box that lands on whole micrometres would round-trip
        // through a truncating formatter as happily as through a correct one.
        var view = new LayoutView { DbuPerMicron = dbuPerMicron, DisplayUnit = unit, SnapDbu = 1 };
        var box = new Bbox(-3_000_001, -7, 20_000_003, 2_900_009);
        view.Shapes.Add(new RectShape { Layer = Metal1, X1 = box.MinX, Y1 = box.MinY, X2 = box.MaxX, Y2 = box.MaxY });
        LayoutPersistence.SaveToFile(clay, view);

        string window = ExplainJson(clay, "--extents").GetProperty("extents")
                            .GetProperty("window").GetString()!;
        output.WriteLine($"{unit} @ {dbuPerMicron} DBU/µm: {window}");

        long[] back = [.. window.Split(',').Select(t =>
        {
            Assert.True(LayoutUnits.TryParse(t, unit, dbuPerMicron, out long dbu),
                        $"render's own parser refused '{t}'");
            return dbu;
        })];
        Assert.Equal([box.MinX, box.MinY, box.MaxX, box.MaxY], back);
    }

    // ══ gate 6 — extents are hierarchy-correct ════════════════════════════════════════════════════

    /// <summary>
    /// A layout whose only geometry is inside a placed sub-cell, rotated and arrayed, reports the
    /// TRANSFORMED box — checked against <c>CellHierarchy.InstanceBbox</c>, which is the existing
    /// answer and the one the renderer's own culling uses.
    /// </summary>
    [Fact]
    public void Extents_OfAPlacedRotatedArray_MatchTheHierarchyWalk()
    {
        var ws = BuildHierarchicalWorkspace(rotate: true, topShape: false);

        var view = LayoutPersistence.LoadFromFile(ws.Clay);
        var expected = CellHierarchy.InstanceBbox(
            view.Instances[0], CellHierarchy.BaseDirOfDocument(ws.Clay));
        Assert.False(expected.IsEmpty);

        var got = ExplainJson(ws.Clay, "--extents").GetProperty("extents");
        double metresPerDbu = 1e-6 / view.DbuPerMicron;

        Assert.Equal(expected.MinX * metresPerDbu, got.GetProperty("x0").GetDouble(), 15);
        Assert.Equal(expected.MinY * metresPerDbu, got.GetProperty("y0").GetDouble(), 15);
        Assert.Equal(expected.MaxX * metresPerDbu, got.GetProperty("x1").GetDouble(), 15);
        Assert.Equal(expected.MaxY * metresPerDbu, got.GetProperty("y1").GetDouble(), 15);
    }

    // ══ gate 7 — the unit is present and the scale is right ═══════════════════════════════════════

    /// <summary>
    /// R-rnd3-8, and the 2 Hz bug in its geometric form: a layout whose technology declares a
    /// non-default DBU-per-micrometre reports extents a hand-computed metre value agrees with.
    ///
    /// <para><b>Hand-computed, deliberately</b> — the shape below is 400 micrometres wide by
    /// construction, so the answer must be 4.0e-4 m whatever the DBU scale is, and a report that
    /// forwarded raw DBU or forgot the scale would come back three or four orders of magnitude out
    /// while still looking entirely normal.</para>
    /// </summary>
    [Theory]
    [InlineData(1000)]   // the default
    [InlineData(10)]     // a coarse grid — 100x fewer DBU for the same physical size
    public void Extents_AreBaseSI_WhateverTheDbuScale(int dbuPerMicron)
    {
        string dir = Path.Combine(_root, "scale" + dbuPerMicron);
        Directory.CreateDirectory(dir);
        string clay = Path.Combine(dir, "Scaled.clay");

        var view = new LayoutView { DbuPerMicron = dbuPerMicron, DisplayUnit = LayoutUnit.Um, SnapDbu = dbuPerMicron };
        view.Shapes.Add(new RectShape
        {
            Layer = Metal1, X1 = 0, Y1 = 0,
            X2 = 400 * dbuPerMicron,   // 400 µm, said in this document's own DBU
            Y2 = 200 * dbuPerMicron,
        });
        LayoutPersistence.SaveToFile(clay, view);

        var got = ExplainJson(clay, "--extents").GetProperty("extents");

        Assert.Equal(400e-6, got.GetProperty("width").GetDouble(),  15);
        Assert.Equal(200e-6, got.GetProperty("height").GetDouble(), 15);
        Assert.Equal("m",  got.GetProperty("unit").GetString());
        Assert.Equal(1e-6, got.GetProperty("scale").GetDouble(), 15);   // the DISPLAY unit's scale
    }

    /// <summary>R-rnd3-10. A document with no geometry has NO extents. <c>(0,0,0,0)</c> is a point at
    /// the origin — a different fact, and one a caller would happily divide by.</summary>
    [Fact]
    public void Extents_OfAnEmptyDocument_AreEmptyRatherThanAZeroBox()
    {
        string dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);
        string clay = Path.Combine(dir, "Empty.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um });

        var got = ExplainJson(clay, "--extents").GetProperty("extents");
        Assert.True(got.GetProperty("empty").GetBoolean());
        Assert.False(got.TryGetProperty("x0", out _));
        Assert.False(got.TryGetProperty("width", out _));
    }

    /// <summary>A schematic's and a symbol's coordinates are dimensionless design units and are SAID
    /// to be, rather than quietly emitted as if they were metres (R-rnd3-8). The spelling is
    /// <c>render</c>'s own — two spellings of one fact across two verbs is the drift this series
    /// exists to prevent.</summary>
    [Fact]
    public void Extents_OfASchematicOrSymbol_AreSaidToBeDimensionless()
    {
        var ws = BuildWorkspace();
        foreach (string path in new[] { ws.Csch, ws.Csym })
        {
            var got = ExplainJson(path, "--extents").GetProperty("extents");
            Assert.Equal("design-units", got.GetProperty("unit").GetString());
            Assert.Equal(1.0, got.GetProperty("scale").GetDouble());
        }

        // A symbol's box is the GEOMETRY — a fit adds room for the pin names, which are drawn in
        // pixels, and the field's own note says so rather than reporting a zoom-dependent number.
        var sym = ExplainJson(ws.Csym, "--extents").GetProperty("extents");
        Assert.Contains("pixels", sym.GetProperty("note").GetString());

        // And the fit genuinely is solved on the WIDER box: the render's own viewport reaches outside
        // the geometry it reports as the extents, which is the room the pin names needed. Without this
        // the two boxes agreeing would be indistinguishable from there being only one.
        var render = RenderJson(ws.Csym, "--fit");
        var vp = render.GetProperty("viewport");
        double geomW = sym.GetProperty("x1").GetDouble() - sym.GetProperty("x0").GetDouble();
        double vpW   = vp.GetProperty("x1").GetDouble() - vp.GetProperty("x0").GetDouble();
        output.WriteLine($"symbol: geometry {geomW}, fitted viewport {vpW}");
        Assert.True(vpW > geomW, "a fitted symbol page must be wider than the geometry — the pin names live in the difference");
    }

    // ══ gate 8 — explain still writes nothing ═════════════════════════════════════════════════════

    /// <summary>R-aut4-6 / §10.1, now with three more options that read considerably more of the tree
    /// than the old ones did. Nothing is created, nothing is touched, and nothing lands in
    /// <c>outputs</c>.</summary>
    [Fact]
    public void TheThreeQuestions_WriteNothing()
    {
        var ws = BuildHierarchicalWorkspace();
        var before = Snapshot(ws.Root);

        foreach (string[] args in new[]
                 {
                     new[] { "--cells" }, ["--cells", "--all"],
                     ["--layers"], ["--extents"],
                 })
        {
            var run = RunCli([.. new[] { "explain", args[0] == "--layers" || args[0] == "--extents" ? ws.Clay : ws.Root },
                              .. args, "--json"]);
            Assert.Equal(0, run.ExitCode);
            Assert.Empty(JsonDocument.Parse(run.StdOut).RootElement.GetProperty("outputs").EnumerateArray());
        }

        Assert.Equal(before, Snapshot(ws.Root));
    }

    // ══ the refusals ══════════════════════════════════════════════════════════════════════════════

    /// <summary>R-rnd3-2: the three join the "refused together rather than ordered" rule rather than
    /// getting an exception from it. Six questions obey the same rule as three.</summary>
    [Fact]
    public void MoreThanOneOfTheSixQuestions_IsRefused()
    {
        var ws = BuildWorkspace();
        foreach (string[] pair in new[]
                 {
                     new[] { "--cells", "--layers" },
                     ["--layers", "--extents"],
                     ["--cells", "--analysis"],
                 })
        {
            var run = RunCli("explain", ws.Clay, pair[0], pair[1], "--json");
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("explain.args.one-question", Ids(run));
        }

        // --all is --cells' own modifier and means nothing beside anything else.
        var stray = RunCli("explain", ws.Clay, "--layers", "--all", "--json");
        Assert.Equal(1, stray.ExitCode);
        Assert.Contains("explain.args.all-needs-cells", Ids(stray));
    }

    /// <summary>R-rnd0-6: a question the GUI would have asked in a dialog is a refusal LISTING the
    /// choices and naming the flag that answers it — the same refusal <c>render</c> gives, because a
    /// caller uses the two together.</summary>
    [Fact]
    public void ExtentsOnAMultiViewCellFolder_RefusesAndNamesTheFlag()
    {
        var ws = BuildWorkspace();

        var refused = RunCli("explain", ws.CellDir, "--extents", "--json");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("explain.cell.view-required", Ids(refused));

        // And naming one answers.
        var answered = RunCli("explain", ws.CellDir, "--extents", "--view", "layout", "--json");
        Assert.Equal(0, answered.ExitCode);
    }

    /// <summary>A question about geometry or drawing layers, asked of a path that holds neither, is
    /// named rather than answered emptily.</summary>
    [Fact]
    public void TheQuestionsRefuse_WhereTheDocumentCannotAnswerThem()
    {
        var ws = BuildWorkspace();

        var layersOnSchematic = RunCli("explain", ws.Csch, "--layers", "--json");
        Assert.Equal(1, layersOnSchematic.ExitCode);
        Assert.Contains("explain.option.wrong-kind", Ids(layersOnSchematic));

        var extentsOnTech = RunCli("explain", ws.Tech, "--extents", "--json");
        Assert.Equal(1, extentsOnTech.ExitCode);
        Assert.Contains("explain.option.wrong-kind", Ids(extentsOnTech));

        var cellsOnClay = RunCli("explain", ws.Clay, "--cells", "--json");
        Assert.Equal(1, cellsOnClay.ExitCode);
        Assert.Contains("explain.option.wrong-kind", Ids(cellsOnClay));
    }

    // ── reading the report ───────────────────────────────────────────────────

    /// <summary>The <c>explain</c> payload of a successful run.</summary>
    private JsonElement ExplainJson(string path, params string[] args)
    {
        var run = RunCli([.. new[] { "explain", path }, .. args, "--json"]);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        return JsonDocument.Parse(run.StdOut).RootElement
                           .GetProperty("result").GetProperty("explain").Clone();
    }

    private JsonElement RenderJson(string path, params string[] args)
    {
        string outPath = Path.Combine(_root, "probe-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        var run = RunCli([.. new[] { "render", path, "-o", outPath }, .. args, "--json"]);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        return JsonDocument.Parse(run.StdOut).RootElement
                           .GetProperty("result").GetProperty("render").Clone();
    }

    private static JsonElement[] Cells(JsonElement explain)
        => [.. explain.GetProperty("cells").EnumerateArray()];

    private static string[] CellFolders(JsonElement explain)
        => [.. Cells(explain).Select(c => c.GetProperty("folder").GetString()!).Order(StringComparer.Ordinal)];

    /// <summary>Every cell folder <c>check</c>'s own walk opened a document inside — read out of its
    /// per-document report rather than re-derived, so this is that verb's answer and not a copy.</summary>
    private string[] CheckedCellFolders(string root)
    {
        var run = RunCli("check", root, "--json");
        var docs = JsonDocument.Parse(run.StdOut).RootElement
                       .GetProperty("result").GetProperty("check").GetProperty("documents");

        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in docs.EnumerateArray())
        {
            if (d.GetProperty("kind").GetString() != "cell") continue;
            dirs.Add(Path.GetFullPath(d.GetProperty("path").GetString()!));
        }
        return [.. dirs.Order(StringComparer.Ordinal)];
    }

    private string[] CheckIds(string root) => Ids(RunCli("check", root, "--json"));

    private static string[] Ids(CliRun run)
        => [.. JsonDocument.Parse(run.StdOut).RootElement.GetProperty("diagnostics").EnumerateArray()
                  .Select(d => d.GetProperty("id").GetString()!)];

    private static Dictionary<string, DateTime> Snapshot(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.Ordinal);

    // ── fixtures ─────────────────────────────────────────────────────────────

    private sealed record Workspace(string Root, string CellDir, string Clay, string Csch, string Csym, string Tech);

    private Workspace BuildWorkspace(string name = "Demo")
    {
        string root = Path.Combine(_root, name);
        string cell = Path.Combine(root, "Stage1");
        Directory.CreateDirectory(Path.Combine(cell, "layout"));
        Directory.CreateDirectory(Path.Combine(cell, "schematic"));
        Directory.CreateDirectory(Path.Combine(cell, "symbol"));
        Directory.CreateDirectory(Path.Combine(root, "tech"));

        string tech = Path.Combine(root, "tech", "Fixture.ctech");
        TechPersistence.SaveToFile(tech, TechFixture());
        WorkspacePersistence.SaveToFile(
            Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = Path.Combine("tech", "Fixture.ctech") });

        string clay = Path.Combine(cell, "layout", "Stage1.clay");
        LayoutPersistence.SaveToFile(clay, LayoutFixture());

        string csch = Path.Combine(cell, "schematic", "Stage1.csch");
        SchematicPersistence.SaveToFile(csch, SchematicFixture(), "Stage1");

        string csym = Path.Combine(cell, "symbol", "Stage1.csym");
        SymbolPersistence.SaveToFile(csym, SymbolFixture());

        return new Workspace(root, cell, clay, csch, csym, tech);
    }

    /// <summary>
    /// One cell per interesting branch of <c>CellFolder.ResolvePrimary</c>, plus one holding a file
    /// that is not what its extension says. These are the cases where "which file is this cell's
    /// schematic" is not obvious, which is the whole reason <c>--cells</c> is worth more than
    /// <c>ls</c>.
    /// </summary>
    private string BuildAwkwardWorkspace()
    {
        string root = Path.Combine(_root, "Awkward");
        Directory.CreateDirectory(root);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), new CwsFile());

        // A .ccell naming a primary that is not there.
        string broken = CellFolder.CreateCellFolder(root, "BrokenPrimary");
        WriteSchematic(Path.Combine(CellFolder.SubFolderPath(broken, ViewType.Schematic), "A.csch"));
        WriteSchematic(Path.Combine(CellFolder.SubFolderPath(broken, ViewType.Schematic), "B.csch"));
        CellPersistence.SaveToFile(Path.Combine(broken, CellFolder.CcellFileName),
            new CcellFile { PrimarySchematic = "Gone.csch" });

        // Three files and nothing naming a primary.
        string ambiguous = CellFolder.CreateCellFolder(root, "Ambiguous");
        foreach (string n in new[] { "One.csch", "Two.csch", "Three.csch" })
            WriteSchematic(Path.Combine(CellFolder.SubFolderPath(ambiguous, ViewType.Schematic), n));

        // An EMPTY schematic sub-folder — no view of that type, which is not an error.
        string symbolOnly = CellFolder.CreateCellFolder(root, "SymbolOnly");
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(symbolOnly, ViewType.Symbol), "SymbolOnly.csym"),
            SymbolFixture());

        // A sole file that primacy resolves to and the validator rejects.
        string defective = CellFolder.CreateCellFolder(root, "Defective");
        File.WriteAllText(
            Path.Combine(CellFolder.SubFolderPath(defective, ViewType.Schematic), "Defective.csch"),
            "{ \"NotASchematic\": true }");

        // A cell a folder deep — the ordinary library arrangement, and the case a walk that only
        // looked one level down would miss.
        string nested = Path.Combine(root, "library");
        Directory.CreateDirectory(nested);
        string deep = CellFolder.CreateCellFolder(nested, "Nested");
        WriteSchematic(Path.Combine(CellFolder.SubFolderPath(deep, ViewType.Schematic), "Nested.csch"));

        // And one under the reserved folder, which `--all` includes and `check` walks.
        string gen = CellFolder.CreateCellFolder(
            Path.Combine(root, ReservedFolders.GeneratedCells), "MLIN_9f3c1a02");
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(gen, ViewType.Layout), "MLIN_9f3c1a02.clay"),
            LayoutFixture());

        return root;
    }

    private readonly record struct Hierarchy(string Root, string Clay);

    /// <param name="rotate">Rotates and mirrors the placement, so the reported box is the TRANSFORMED
    /// one rather than the sub-cell's own.</param>
    /// <param name="topShape">Whether the top cell draws anything itself. False is the case gate 6
    /// wants: a layout whose ONLY geometry is inside a placed sub-cell.</param>
    private Hierarchy BuildHierarchicalWorkspace(bool rotate = false, bool topShape = true)
    {
        string root = Path.Combine(_root, "Hier" + (rotate ? "R" : "") + (topShape ? "T" : ""));
        Directory.CreateDirectory(Path.Combine(root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(root, "tech", "Fixture.ctech"), TechFixture());
        WorkspacePersistence.SaveToFile(
            Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = Path.Combine("tech", "Fixture.ctech") });

        string leaf = CellFolder.CreateCellFolder(root, "Leaf");
        var leafView = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = Dbu };
        leafView.Shapes.Add(new RectShape { Layer = Metal1, X1 = 0, Y1 = 0, X2 = 100 * Dbu, Y2 = 50 * Dbu });
        leafView.Shapes.Add(new RectShape { Layer = Metal2, X1 = 0, Y1 = 60 * Dbu, X2 = 100 * Dbu, Y2 = 90 * Dbu });
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(leaf, ViewType.Layout), "Leaf.clay"), leafView);

        string top = CellFolder.CreateCellFolder(root, "Top");
        var topView = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = Dbu };
        if (topShape)
            topView.Shapes.Add(new RectShape { Layer = Metal1, X1 = 0, Y1 = 0, X2 = 200 * Dbu, Y2 = 20 * Dbu });
        topView.Instances.Add(new LayoutInstance
        {
            CellRef  = Path.Combine("..", "..", "Leaf"),
            X        = 300 * Dbu, Y = 0,
            Rows     = 2, Cols = 3,
            PitchX   = 150 * Dbu, PitchY = 120 * Dbu,
            MirrorX  = rotate,
        });
        if (rotate) topView.Instances[0].RotationDegrees = 90;

        string clay = Path.Combine(CellFolder.SubFolderPath(top, ViewType.Layout), "Top.clay");
        LayoutPersistence.SaveToFile(clay, topView);
        return new Hierarchy(root, clay);
    }

    private static Technology TechFixture() => new()
    {
        Name = "RND-3 fixture",
        DefaultDisplayUnit = LayoutUnit.Um,
        Layers =
        [
            new LayerDef { Key = Metal1, Name = "Metal1", Color = new Rgba(200, 30, 30), FillOpacity = 0.5, ZOrder = 3 },
            new LayerDef { Key = Metal2, Name = "Metal2", Color = new Rgba(30, 120, 200), FillOpacity = 0.5, ZOrder = 2 },
            new LayerDef { Key = Silk,   Name = "Silk",   Color = new Rgba(230, 230, 230), FillOpacity = 0.5, ZOrder = 1 },
        ],
    };

    private static LayoutView LayoutFixture()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = Dbu };
        view.Shapes.Add(new RectShape { Layer = Metal1, X1 = 0, Y1 = 0, X2 = 400 * Dbu, Y2 = 200 * Dbu });
        view.Shapes.Add(new RectShape { Layer = Metal2, X1 = 500 * Dbu, Y1 = 50 * Dbu, X2 = 700 * Dbu, Y2 = 150 * Dbu });
        return view;
    }

    private static SchematicEditModel SchematicFixture()
    {
        var m = new SchematicEditModel { GridSize = 100 };
        m.Components.Add(new EditableComponent
        {
            Symbol = SymbolKind.Resistor, X = 200, Y = 200, InstanceName = "R1",
            Parameters = { new EditableParameter { Name = "R", Expression = "50" } },
        });
        m.Components.Add(new EditableComponent
        {
            Symbol = SymbolKind.Capacitor, X = 600, Y = 200, InstanceName = "C1",
            Parameters = { new EditableParameter { Name = "C", Expression = "1p" } },
        });
        return m;
    }

    private static Symbol SymbolFixture() => new(
        [
            new LinePrimitive { X1 = 20, Y1 = 20, X2 = 180, Y2 = 20 },
            new LinePrimitive { X1 = 180, Y1 = 20, X2 = 180, Y2 = 100 },
            new LinePrimitive { X1 = 180, Y1 = 100, X2 = 20, Y2 = 100 },
            new LinePrimitive { X1 = 20, Y1 = 100, X2 = 20, Y2 = 20 },
        ],
        [new SymbolPin(20, 60, 0, "in"), new SymbolPin(180, 60, 1, "out")]);

    private static void WriteSchematic(string path)
        => File.WriteAllText(path, SchematicPersistence.Serialize(new SchematicEditModel(), "X"));

    // ── driving the verb ─────────────────────────────────────────────────────

    private readonly record struct CliRun(int ExitCode, string StdOut, string StdErr);

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
                typeof(ExplainQueryCliVerbTests).Assembly)
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
