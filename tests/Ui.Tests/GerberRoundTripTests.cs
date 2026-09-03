// Gate for docs/sonnet-briefs/brief-L4h-gerber-import-ui-and-round-trip.md §3 — the round trip, and
// the phase's real deliverable.
//
// R-L4h-15: the fixture is a DESIGN, not a file. It is built from LayoutShapes, written by L4c and
// read back by L4e/L4f/L4g, so this gate tests our two sides against each other with no third-party
// file in the loop — which is exactly its purpose and exactly its limitation (R-L4h-16). What it can
// catch that nothing else here can is a reader and a writer being wrong in the SAME direction; what it
// cannot say anything about is a dialect neither side emits.
//
// The claim being proven is not "lossless", which is false, but CLOSED AFTER ONE PASS: whatever the
// first cycle collapses, every later cycle preserves exactly.
//
// Gate 18: COUNTERS ONLY. There is no wall-clock assertion anywhere in this file.

using Clipper2Lib;

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

public class GerberRoundTripTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gerber-roundtrip-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey Drill = new(3, 0);

    // ── The fixture (R-L4h-15) ───────────────────────────────────────────────────────────────────

    private static Technology FixtureTech() => new()
    {
        Name = "rt",
        Layers =
        [
            new LayerDef
            {
                Key = Top, Name = "Top Copper", Color = new Rgba(0xC8, 0x7A, 0x3E), ZOrder = 0,
                Interchange = new InterchangeMapping(null, null, null, "GTL", "Copper,L1,Top,Signal"),
            },
            new LayerDef
            {
                Key = Bot, Name = "Bottom Copper", Color = new Rgba(0x3E, 0x7A, 0xC8), ZOrder = 10,
                Interchange = new InterchangeMapping(null, null, null, "GBL", "Copper,L2,Bot,Signal"),
            },
            // A real drill layer: a StackupKind.Via entry names it below, which is what makes a bare
            // circle on it a HOLE and a ViaShape's Layer field mean the barrel. It carries no artwork
            // of its own in this fixture and therefore gets no Gerber file — see
            // Vias_ComeBackAsVias_… and ABareCircleOnADrillLayer_… for both halves of why.
            new LayerDef
            {
                Key = Drill, Name = "Drill", Color = new Rgba(0x88, 0x88, 0x88), ZOrder = 20,
                Interchange = new InterchangeMapping(null, null, null, "DRL", "Plated,1,2,PTH"),
            },
        ],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top Copper", DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom Copper", DrawingLayers = [Bot] },
                new StackupLayer { Kind = StackupKind.Via, Name = "Drill", DrawingLayers = [Drill], Fill = ViaFillKind.Plated },
            ],
        },
    };

    /// <summary>
    /// Every row of §2's lossy table, plus arcs, holes, nets and three drill diameters.
    ///
    /// <para>The TOP layer never paints clear, so its primitives must survive as primitives
    /// (R-L4h-13). The BOTTOM layer carries a polygon with a hole, which L4c writes as a <c>%LPC</c>
    /// clear region and L4e therefore composites — <b>both polarity paths in one fixture</b>
    /// (R-L4h-14), because a fixture with only the first kind cannot distinguish "composites only
    /// where needed" from "never composites", and one with only the second cannot distinguish it from
    /// "always composites".</para>
    /// </summary>
    private static void PopulateFixture(LayoutView v)
    {
        // ── Top: no clear polarity anywhere ───────────────────────────────────────────────────
        v.Shapes.Add(new CircleShape { Layer = Top, Cx = 1_000_000, Cy = 1_000_000, R = 200_000, Net = "VCC" });
        v.Shapes.Add(new RectShape { Layer = Top, X1 = 2_000_000, Y1 = 0, X2 = 3_000_000, Y2 = 500_000, Net = "GND" });
        v.Shapes.Add(new PathShape
        {
            Layer = Top, Xy = [0, 2_000_000, 1_000_000, 2_000_000, 1_500_000, 2_500_000],
            Width = 150_000, End = PathEndStyle.Round, Net = "SIG",
        });
        v.Shapes.Add(new RoundedRectShape
        {
            Layer = Top, X1 = 4_000_000, Y1 = 0, X2 = 5_000_000, Y2 = 600_000, CornerRadius = 100_000,
        });
        // A non-round end style: L4c writes it as one or more plain regions, never as a stroke.
        v.Shapes.Add(new PathShape
        {
            Layer = Top, Xy = [4_000_000, 2_000_000, 5_000_000, 2_000_000],
            Width = 200_000, End = PathEndStyle.Square, Net = "SIG",
        });
        // An arc edge, so G02/G03 and its I/J offsets are in the loop.
        v.Shapes.Add(new CurveShape
        {
            Layer = Top,
            Xy = [6_000_000, 0, 7_000_000, 0, 7_000_000, 1_000_000, 6_000_000, 1_000_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        });

        // ── Bottom: a polygon with a HOLE — the clear-polarity path ───────────────────────────
        v.Shapes.Add(new PolygonShape
        {
            Layer = Bot,
            Xy = [0, 0, 3_000_000, 0, 3_000_000, 3_000_000, 0, 3_000_000],
            // Wound OPPOSITE the outer ring, which is this codebase's own hole convention
            // (LayoutClipper is FillRule.NonZero everywhere; FromClipperTree emits counter-wound holes).
            Holes = [[1_000_000, 1_000_000, 1_000_000, 2_000_000, 2_000_000, 2_000_000, 2_000_000, 1_000_000]],
            Net = "GND",
        });

        // ── Vias: THREE distinct drill diameters (R-L4h-11 — a single-tool fixture cannot fail
        //    the drill-exactness test, which is the whole reason it gets its own gate line) ─────
        v.Shapes.Add(new ViaShape
        {
            Layer = Drill, LandingLayer = Top, X = 1_000_000, Y = 4_000_000,
            PadSize = 400_000, DrillSize = 200_000, Net = "VCC",
        });
        v.Shapes.Add(new ViaShape
        {
            Layer = Drill, LandingLayer = Top, X = 2_000_000, Y = 4_000_000,
            PadSize = 500_000, DrillSize = 300_000, Net = "GND",
        });
        v.Shapes.Add(new ViaShape
        {
            Layer = Drill, LandingLayer = Top, X = 3_000_000, Y = 4_000_000,
            PadSize = 600_000, DrillSize = 400_000, Net = "SIG",
        });
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    private string CreateCell(string parentDir, string name, Technology tech, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(parentDir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string techPath = Path.Combine(parentDir, name + ".ctech");
        TechPersistence.SaveToFile(techPath, tech);

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.GetRelativePath(layoutDir, techPath) };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, $"{name}.clay"), view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    private static LayoutView LoadView(string cellDir) =>
        LayoutPersistence.LoadFromFile(
            Directory.EnumerateFiles(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "*.clay").Single());

    /// <summary>One export, into <c>&lt;root&gt;/&lt;cycle&gt;/board</c>. The leaf folder is named
    /// "board" every time on purpose: the folder's own name is what a folder import calls the cell it
    /// creates, so three exports into three differently-named folders would produce three differently
    /// named cells and the file sets would stop being comparable — which is R-L4g-7's point about
    /// <c>GerberSuffix</c> applied one level up.</summary>
    private string Export(string cycle, string cellDir, Technology? tech, string cellName)
    {
        var plan = GerberExport.Analyze(cellDir, tech, Dbu, LoadView(cellDir), null);
        Assert.True(plan.CanWrite, string.Join("; ", plan.Diagnostics));
        string outDir = Path.Combine(_root, cycle, cellName);
        GerberExport.Write(outDir, cellName, plan);
        return outDir;
    }

    private GerberImport.ImportResult Import(string sourceDir, string parentName)
    {
        string parent = Path.Combine(_root, parentName);
        Directory.CreateDirectory(parent);
        // The real folder-entry path, prompt and all — a round trip that bypassed it would not be
        // proving the thing the menu entry actually runs.
        var result = GerberImportEntry.RunFolder(sourceDir, parent, null, Dbu);
        Assert.False(result.Cancelled, string.Join("\n", result.Messages));
        return result;
    }

    /// <summary>Three exports and two imports, run once per test. The design goes out (export1), comes
    /// back (import1), goes out again (export2), comes back again (import2) and goes out a third time
    /// (export3) — enough to distinguish "collapsed once and then stable" from "still drifting".</summary>
    private sealed record Cycles(
        string OriginalCellDir, Technology OriginalTech,
        string Export1, GerberImport.ImportResult Import1,
        string Export2, GerberImport.ImportResult Import2,
        string Export3);

    private Cycles RunCycles(Action<LayoutView>? populate = null, Technology? tech = null)
    {
        tech ??= FixtureTech();
        string src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        var cellDir = CreateCell(src, "board", tech, populate ?? PopulateFixture);

        var e1 = Export("export1", cellDir, tech, "board");
        var i1 = Import(e1, "import1");
        var e2 = Export("export2", i1.CellDir!, i1.Technology, Path.GetFileName(i1.CellDir!));
        var i2 = Import(e2, "import2");
        var e3 = Export("export3", i2.CellDir!, i2.Technology, Path.GetFileName(i2.CellDir!));

        return new Cycles(cellDir, tech, e1, i1, e2, i2, e3);
    }

    private static IReadOnlyList<string> FilesIn(string dir) =>
        [.. Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];

    // ── Gate 10 — cycle 1, geometric closure (R-L4h-9) ───────────────────────────────────────────

    /// <summary>The flattening both sides are measured at. Fixed rather than technology-resolved, so
    /// the two designs' curves are approximated identically — a different tolerance on the two sides
    /// would report a difference belonging to the measurement rather than to the round trip.</summary>
    private const long XorTolDbu = 100;   // 0.1 nm at DbuPerMicron = 1000

    /// <summary>
    /// Every layer's artwork as Clipper paths, keyed by the TECHNOLOGY LAYER NAME rather than by
    /// <see cref="LayerKey"/> — an import mints its own keys in file order, so two designs that agree
    /// perfectly still number their layers differently, while the name is what the file set actually
    /// carries across (the identity cascade reads it from the job file).
    ///
    /// <para>Expansion goes through <c>DrcRegions.Expand</c>, the SAME via decomposition every exporter
    /// applies (R-via-9), rather than a second one written here — a measurement that re-implements the
    /// thing it measures can agree with itself and still be wrong.</para>
    /// </summary>
    private static Dictionary<string, Paths64> GeometryByLayerName(LayoutView view, Technology tech)
    {
        var nameOf = tech.Layers.ToDictionary(l => l.Key, l => l.Name);
        var byName = new Dictionary<string, Paths64>(StringComparer.Ordinal);

        foreach (var shape in view.Shapes)
            CircuitRF.Ui.Layout.Drc.DrcRegions.Expand(
                shape, tech, _ => XorTolDbu,
                (key, _, paths) =>
                {
                    string name = nameOf.TryGetValue(key, out var n) ? n : key.ToString();
                    if (!byName.TryGetValue(name, out var acc)) byName[name] = acc = [];
                    acc.AddRange(paths);
                });

        return byName.ToDictionary(kv => kv.Key, kv => Clipper.Union(kv.Value, FillRule.NonZero));
    }

    [Fact]
    public void Cycle1_PerLayerXorAgainstTheOriginal_IsEmptyInDbu_Exactly()
    {
        var c = RunCycles();

        var original = GeometryByLayerName(LoadView(c.OriginalCellDir), c.OriginalTech);
        var reimported = GeometryByLayerName(LoadView(c.Import1.CellDir!), c.Import1.Technology!);

        // Every layer of the original is present, by name, in what came back — a layer that vanished
        // would otherwise XOR against an empty set and be reported as "different" rather than "gone",
        // which is a much less useful sentence.
        Assert.Equal(original.Keys.Order(), reimported.Keys.Order());

        foreach (var (name, before) in original)
        {
            var after = reimported[name];
            var xor = Clipper.Xor(before, after, FillRule.NonZero);

            // EXACT, not toleranced (R-L4h-9). A tolerance would hide precisely the systematic errors
            // worth catching — a unit scale off by a factor, an arc centre resolved to the wrong
            // candidate, a hole subtracted from the wrong outline.
            Assert.True(xor.Count == 0,
                $"Layer \"{name}\" does not close: the XOR of the re-imported design against the " +
                $"original has {xor.Count} path(s) and {Clipper.Area(xor):0} DBU² of area " +
                $"(original {Clipper.Area(before):0}, re-imported {Clipper.Area(after):0}).");
        }
    }

    // ── Gate 11 — cycle 2 onward, byte identity (R-L4h-10) ───────────────────────────────────────

    /// <summary>The one field that legitimately differs between two exports: the creation timestamp
    /// the files carry by design. Named, rather than tolerated by a loose comparison — the same
    /// discipline the CLI's EM verb gate already applies against <c>EmRunService</c>.</summary>
    private static string[] LinesWithoutCreationDate(string path) =>
        [.. File.ReadAllLines(path).Where(l =>
            !l.StartsWith("%TF.CreationDate", StringComparison.Ordinal) &&
            !l.TrimStart().StartsWith("\"CreationDate\"", StringComparison.Ordinal))];

    private static string? FirstDifference(string[] a, string[] b)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string x = i < a.Length ? a[i] : "<end of file>";
            string y = i < b.Length ? b[i] : "<end of file>";
            if (!string.Equals(x, y, StringComparison.Ordinal)) return $"line {i + 1}: \"{x}\" vs \"{y}\"";
        }
        return null;
    }

    [Fact]
    public void Cycle2AndCycle3_AreByteIdentical_ExceptTheCreationDateTheFilesCarryByDesign()
    {
        var c = RunCycles();

        Assert.Equal(FilesIn(c.Export2).Select(Path.GetFileName), FilesIn(c.Export3).Select(Path.GetFileName));

        foreach (var file in FilesIn(c.Export2))
        {
            string name = Path.GetFileName(file);
            string other = Path.Combine(c.Export3, name);
            var diff = FirstDifference(LinesWithoutCreationDate(file), LinesWithoutCreationDate(other));
            Assert.True(diff is null, $"export2 and export3 differ in {name} at {diff}");
        }
    }

    /// <summary>
    /// The measurement R-L4h-9's completion note asks for, stated as an assertion rather than left as
    /// prose: <b>which cycle each property stabilizes at.</b> Everything this fixture can lose is lost
    /// in cycle 1 — so export1 and export2 already agree everywhere except the one place §2's table
    /// says they cannot: the composited layer's per-object net names.
    /// </summary>
    [Fact]
    public void Cycle1_AlreadyReproducesEveryFile_ExceptTheCompositedLayersNetNames()
    {
        var c = RunCycles();

        Assert.Equal(FilesIn(c.Export1).Select(Path.GetFileName), FilesIn(c.Export2).Select(Path.GetFileName));

        var differing = new List<string>();
        foreach (var file in FilesIn(c.Export1))
        {
            string name = Path.GetFileName(file);
            var diff = FirstDifference(
                LinesWithoutCreationDate(file), LinesWithoutCreationDate(Path.Combine(c.Export2, name)));
            if (diff is not null) differing.Add($"{name} ({diff})");
        }

        // board.GBL is the layer that painted clear and was therefore composited; its %TO.N net
        // attribute is gone by cycle 2, which is the documented, permanent loss and not a defect.
        Assert.Equal(
            ["board.GBL (line 8: \"%TO.N,GND*%\" vs \"%TD.N*%\")"],
            differing);
    }

    // ── Gate 12 — drill data is exact at EVERY cycle (R-L4h-11) ──────────────────────────────────

    private static (int Tools, long[] Diameters, (long X, long Y, long D)[] Hits) DrillOf(string exportDir)
    {
        string path = FilesIn(exportDir).Single(f => f.EndsWith(".drl", StringComparison.OrdinalIgnoreCase));
        using var stream = File.OpenRead(path);
        var read = ExcellonReader.Read(stream, Dbu);
        Assert.Null(read.Refusal);
        return (read.Tools.Count,
                [.. read.Tools.Select(t => t.DiameterDbu).Order()],
                [.. read.Hits.Select(h => (h.X, h.Y, h.DiameterDbu)).OrderBy(h => h.X).ThenBy(h => h.Y)]);
    }

    [Fact]
    public void DrillData_IsIdenticalAtEveryCycle_ToolsDiametersAndTheFullHitSet()
    {
        var c = RunCycles();

        var d1 = DrillOf(c.Export1);
        var d2 = DrillOf(c.Export2);
        var d3 = DrillOf(c.Export3);

        // R-L4h-11: the fixture must carry more than one tool diameter, or this test cannot fail.
        Assert.Equal(3, d1.Tools);
        Assert.Equal([200_000L, 300_000L, 400_000L], d1.Diameters);

        Assert.Equal(d1.Tools, d2.Tools);
        Assert.Equal(d1.Tools, d3.Tools);
        Assert.Equal(d1.Diameters, d2.Diameters);
        Assert.Equal(d1.Diameters, d3.Diameters);
        Assert.Equal(d1.Hits, d2.Hits);
        Assert.Equal(d1.Hits, d3.Hits);
    }

    // ── Gate 13 — vias survive as vias (R-L4h-12) ────────────────────────────────────────────────

    [Fact]
    public void Vias_ComeBackAsVias_WithTheirPadAndDrillSizes_ProvenByReExport()
    {
        var c = RunCycles();

        var vias = LoadView(c.Import1.CellDir!).Shapes.OfType<ViaShape>()
            .OrderBy(v => v.X).ToList();
        Assert.Equal(3, vias.Count);
        Assert.Equal([(400_000L, 200_000L), (500_000L, 300_000L), (600_000L, 400_000L)],
                     vias.Select(v => (v.PadSize, v.DrillSize)));

        // ...and NOT as a circle plus an orphaned hole.
        Assert.Empty(LoadView(c.Import1.CellDir!).Shapes.OfType<CircleShape>()
            .Where(s => vias.Any(v => v.X == s.Cx && v.Y == s.Cy)));

        // R-L4h-12 / L4d's R-L4d-10 discipline: barrel and landing are exactly the pair that reads
        // correctly while rendering wrong, so the orientation is proven by EXPORTING and comparing,
        // never by reading the two fields back. Both halves of the via — the copper pad in the copper
        // file and the hit in the drill file — must land where they did the first time.
        foreach (var name in (string[])["board.GTL", "board.drl"])
        {
            var diff = FirstDifference(
                LinesWithoutCreationDate(Path.Combine(c.Export1, name)),
                LinesWithoutCreationDate(Path.Combine(c.Export2, name)));
            Assert.True(diff is null, $"{name} changed across the first cycle at {diff}");
        }

        // The pad is copper and is written into the COPPER file; the drill layer has no artwork of its
        // own and therefore gets no Gerber file at all — the only ".drl" in the set is the Excellon.
        // Getting this backwards produces a board whose copper layer has a hole and no annular ring,
        // and (before L4h) an extra copper file the next import read as a second drill layer.
        Assert.Equal(
            ["board.drl", "board.GBL", "board.gbrjob", "board.GTL"],
            FilesIn(c.Export1).Select(Path.GetFileName));
    }

    // ── Gate 14 — shape identity where the format allows it (R-L4h-13) ───────────────────────────

    /// <summary>
    /// Asserted on the TYPES, directly. L4e's R-L4e-9 and R-L4e-13 exist to protect this, and without
    /// a test that names it both will eventually be "simplified" into a uniform polygonize-and-
    /// composite reader that passes every geometric check in this file and quietly destroys the round
    /// trip.
    /// </summary>
    [Fact]
    public void ShapeIdentity_IsPreserved_OnTheLayerThatNeverPaintedClear()
    {
        var c = RunCycles();

        var topKey = c.Import1.Technology!.Layers.Single(l => l.Name == "Top Copper").Key;
        var top = LoadView(c.Import1.CellDir!).Shapes.Where(s => s.Layer == topKey).ToList();

        // A circle flash returns as a CircleShape, of the right radius.
        var circle = Assert.Single(top.OfType<CircleShape>());
        Assert.Equal(200_000, circle.R);

        // A round-capped stroke returns as a PathShape of the right Width.
        var path = Assert.Single(top.OfType<PathShape>());
        Assert.Equal(150_000, path.Width);
        Assert.Equal(PathEndStyle.Round, path.End);

        // The rounded rect and the arc-edged curve keep their ARCS rather than being polygonized
        // (R-L4e-11) — they come back as CurveShapes carrying arc edges, not as PolygonShapes.
        Assert.Equal(2, top.OfType<CurveShape>().Count());
        Assert.All(top.OfType<CurveShape>(), curve =>
            Assert.Contains(curve.Edges ?? [], e => e.Kind == EdgeKind.Arc));

        // The rect and the square-capped path are the two §2 rows that CANNOT come back as themselves:
        // the format writes both as plain regions and has no type to distinguish them.
        Assert.Equal(2, top.OfType<PolygonShape>().Count());
    }

    /// <summary>A rect FLASH — an <c>R</c> aperture flashed with D03 — returns as a
    /// <see cref="RectShape"/>. It cannot come from the round trip, because L4c writes rectangles as
    /// regions and never defines an <c>R</c> aperture, so it is asserted here against a file real
    /// exports do produce. Same claim as R-L4h-13's middle item, one rung further out than the fixture
    /// itself can reach.</summary>
    [Fact]
    public void ARectangularApertureFlash_ComesBackAsARectShape_ThroughTheFullImport()
    {
        string dir = Path.Combine(_root, "flashes");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "board.gtl"),
            "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Copper,L1,Top,Signal*%\n" +
            "%ADD10C,0.400*%\n%ADD11R,0.800X0.600*%\n" +
            "D10*\nX1000000Y1000000D03*\n" +
            "D11*\nX3000000Y1000000D03*\n" +
            "M02*\n");

        string parent = Path.Combine(_root, "flash_import");
        Directory.CreateDirectory(parent);
        var result = GerberImportEntry.RunFolder(dir, parent, null, Dbu);
        Assert.False(result.Cancelled);

        var shapes = LoadView(result.CellDir!).Shapes;
        Assert.Single(shapes.OfType<CircleShape>());
        var rect = Assert.Single(shapes.OfType<RectShape>());
        Assert.Equal(800_000, rect.X2 - rect.X1);
        Assert.Equal(600_000, rect.Y2 - rect.Y1);
    }

    // ── Gate 15 — both polarity paths (R-L4h-14) ─────────────────────────────────────────────────

    [Fact]
    public void TheClearPolarityLayerComposites_AndIsNamedAsComposited_WhileTheOtherKeepsItsPrimitives()
    {
        var c = RunCycles();

        var top = c.Import1.Layers.Single(l => l.LayerName == "Top Copper");
        var bottom = c.Import1.Layers.Single(l => l.LayerName == "Bottom Copper");

        Assert.False(top.Composited);
        Assert.True(bottom.Composited);

        // "and the summary names it" — by file, with the reason, not as a silent flag on a record.
        Assert.Contains(c.Import1.Messages, m =>
            m.Contains("board.GBL", StringComparison.Ordinal) &&
            m.Contains("COMPOSITED", StringComparison.Ordinal));

        // The composite is one polygon with the hole cut out of it, not two overlapping regions.
        var bottomKey = c.Import1.Technology!.Layers.Single(l => l.Name == "Bottom Copper").Key;
        var polygon = Assert.Single(LoadView(c.Import1.CellDir!).Shapes
            .Where(s => s.Layer == bottomKey).OfType<PolygonShape>());
        Assert.NotNull(polygon.Holes);
        Assert.Single(polygon.Holes!);
    }

    // ── Gate 16 — nets (L4e R-L4e-16, R-L4h-9's second half) ─────────────────────────────────────

    [Fact]
    public void ANetName_SurvivesAFullCycle_OnALayerThatWasNotComposited()
    {
        var c = RunCycles();

        var topKey = c.Import1.Technology!.Layers.Single(l => l.Name == "Top Copper").Key;
        var top = LoadView(c.Import1.CellDir!).Shapes.Where(s => s.Layer == topKey).ToList();

        Assert.Equal("VCC", Assert.Single(top.OfType<CircleShape>()).Net);
        Assert.Equal("SIG", Assert.Single(top.OfType<PathShape>()).Net);
        Assert.Contains(top, s => s.Net == "GND");

        // A via carries the net of the pad it was rebuilt from.
        Assert.Equal(["GND", "SIG", "VCC"],
            LoadView(c.Import1.CellDir!).Shapes.OfType<ViaShape>().Select(v => v.Net).Order());
    }

    /// <summary>
    /// R-L4h-9: the one loss on §2's table that was OURS rather than the format's.
    /// <c>GerberWriter.EscapeAttribute</c> replaced <c>*</c>, <c>%</c> and <c>,</c> with <c>_</c>,
    /// because those characters terminate or delimit a block — but the format does not require that,
    /// it defines <c>\uXXXX</c> escapes for exactly this, and <c>GerberReader</c> has undone them since
    /// L4e. So a net named with a comma survived a round trip through a third-party tool and did not
    /// survive one through ours.
    ///
    /// <para>Proven by a failing cycle first, as §2 demands: the name went out as <c>A_B_C_D</c> and
    /// came back as <c>A_B_C_D</c> — a silent, permanent rename after one cycle. The writer now emits
    /// the escape, and <b>files exported before that change carry the underscores.</b></para>
    /// </summary>
    [Fact]
    public void ANetNameCarryingTheFormatsOwnDelimiters_SurvivesAFullCycle()
    {
        var c = RunCycles(v => v.Shapes.Add(new RectShape
        {
            Layer = Top, X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 1_000_000, Net = @"A,B*C%D\E",
        }));

        Assert.Equal(@"A,B*C%D\E", Assert.Single(LoadView(c.Import1.CellDir!).Shapes).Net);

        // The FILE carries the \uXXXX escape, not the literal — which is what makes it readable by
        // anything else, and what the underscore substitution was not.
        Assert.Contains(@"%TO.N,A\u002CB\u002AC\u0025D\u005CE*%",
            File.ReadAllText(Path.Combine(c.Export1, "board.GTL")), StringComparison.Ordinal);
    }

    // ── Gate 17 — the job file (L4g R-L4g-5 rung 0) ──────────────────────────────────────────────

    [Fact]
    public void TheJobFile_SettlesEveryLayersIdentity_WithNoHeuristicAndNoDialog_AndSurvivesTheCycle()
    {
        int dialogs = 0;
        string src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        var tech = FixtureTech();
        var cellDir = CreateCell(src, "board", tech, PopulateFixture);
        var e1 = Export("export1", cellDir, tech, "board");

        Assert.Contains(FilesIn(e1), f => f.EndsWith(".gbrjob", StringComparison.Ordinal));

        string parent = Path.Combine(_root, "import1");
        Directory.CreateDirectory(parent);
        var i1 = GerberImport.Import(
            FilesIn(e1), parent, "board", null, Dbu,
            resolveLayerMapping: rows => { dialogs++; return LayoutLayerMapping.BuildChoices(rows); });

        Assert.False(i1.Cancelled);
        Assert.Equal(0, dialogs);

        // Every layer settled at rung 0 — the job file — and nothing fell through to a name heuristic.
        Assert.All(i1.Layers, l => Assert.Equal(GerberLayerRung.JobFile, l.Rung));
        Assert.All(i1.Layers, l => Assert.False(l.IdentityGuessed));
        Assert.Contains(i1.Messages, m => m.Contains("names 2 file(s) as part of this board", StringComparison.Ordinal));

        // R-L4g-7's GerberSuffix is what makes this load-bearing: without it export2 names its files
        // differently from export1 and the file set stops being comparable at all.
        var e2 = Export("export2", i1.CellDir!, i1.Technology, Path.GetFileName(i1.CellDir!));
        Assert.Equal(FilesIn(e1).Select(Path.GetFileName), FilesIn(e2).Select(Path.GetFileName));

        var diff = FirstDifference(
            LinesWithoutCreationDate(Path.Combine(e1, "board.gbrjob")),
            LinesWithoutCreationDate(Path.Combine(e2, "board.gbrjob")));
        Assert.True(diff is null, $"export2's job file differs from export1's at {diff}");
    }

    /// <summary>
    /// R-via-5's bare circle on a drill layer — a hole drawn as a circle, which is the intuitive and,
    /// for MMIC, genuinely correct way to draw a via. It has to close too, and it did not: the export
    /// wrote it TWICE, as an Excellon hit and as a filled disc in a Gerber file for the drill layer, so
    /// the re-import paired the disc with its own hole into a via and landed a copper pad on the top
    /// layer the design never had. Measured before the fix: the top layer's XOR carried 7.06e10 DBU² of
    /// new copper (exactly the disc's area), the drill layer split into two, and export2 had one fewer
    /// file than export1. The circles now go only in the drill file, which is also what a fab needs —
    /// a Gerber file of copper discs on the drill layer is copper to etch where the hole goes.
    /// </summary>
    [Fact]
    public void ABareCircleOnADrillLayer_ClosesToo_AndIsNotWrittenAsCopper()
    {
        var tech = FixtureTech();
        var c = RunCycles(v =>
        {
            PopulateFixture(v);
            v.Shapes.Add(new CircleShape { Layer = Drill, Cx = 8_000_000, Cy = 8_000_000, R = 150_000 });
        }, tech);

        var before = GeometryByLayerName(LoadView(c.OriginalCellDir), tech);
        var after = GeometryByLayerName(LoadView(c.Import1.CellDir!), c.Import1.Technology!);

        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, a) in before)
        {
            var xor = Clipper.Xor(a, after[name], FillRule.NonZero);
            Assert.True(xor.Count == 0,
                $"Layer \"{name}\" does not close with a bare drill circle in the design: " +
                $"{xor.Count} path(s), {Clipper.Area(xor):0} DBU².");
        }

        // The drill layer gets NO Gerber file — the circle is a hole and lives only in the Excellon.
        Assert.Equal(
            ["board.drl", "board.GBL", "board.gbrjob", "board.GTL"],
            FilesIn(c.Export1).Select(Path.GetFileName));

        // It comes back as a bare circle on the drill layer, not as a fourth via.
        var shapes = LoadView(c.Import1.CellDir!).Shapes;
        Assert.Equal(3, shapes.OfType<ViaShape>().Count());
        var hole = Assert.Single(shapes.OfType<CircleShape>(), s => s.Cx == 8_000_000);
        Assert.Equal(150_000, hole.R);
    }

    // ── The failing cycles this phase was built on, kept as regressions ──────────────────────────

    /// <summary>
    /// A drawing layer whose <c>GerberSuffix</c> is <c>DRL</c> collides case-insensitively with the
    /// Excellon file's own conventional name. Before L4h the layer loop wrote <c>board.DRL</c> and the
    /// drill write then created <c>board.drl</c>, which on a case-insensitive filesystem (macOS,
    /// Windows) CLOBBERED it — a whole layer's copper left the building as a drill file, and the round
    /// trip came back with a board missing a layer. The drill name is now claimed first, so the layer
    /// is the one that gets disambiguated; the drill file keeps the name a fab looks for.
    /// </summary>
    [Fact]
    public void ALayerWhoseSuffixIsDrl_DoesNotLoseItsFileToTheExcellonWrite()
    {
        var mechanical = new LayerKey(4, 0);
        var tech = FixtureTech();
        tech.Layers.Add(new LayerDef
        {
            Key = mechanical, Name = "Mechanical", Color = new Rgba(0x40, 0x40, 0x40), ZOrder = 30,
            Interchange = new InterchangeMapping(null, null, null, "drl", "Other,Mechanical"),
        });

        string src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        var cellDir = CreateCell(src, "board", tech, v =>
        {
            PopulateFixture(v);   // which also writes an Excellon file, because it has vias
            v.Shapes.Add(new RectShape { Layer = mechanical, X1 = 0, Y1 = 8_000_000, X2 = 1_000_000, Y2 = 9_000_000 });
        });

        var names = FilesIn(Export("export1", cellDir, tech, "board")).Select(Path.GetFileName).ToList();

        Assert.Contains("board.drl", names);
        Assert.Contains(names, n => n!.StartsWith("board.drl_", StringComparison.Ordinal));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
