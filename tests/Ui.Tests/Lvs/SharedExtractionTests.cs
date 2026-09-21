// ================================================================
//  SharedExtractionTests.cs — brief-lvs-2-shared-extraction.md §6.
//
//  Brief 2 is a PROMOTION: the copper reading railRF has always performed becomes the API LVS calls
//  too. So the gate has two halves and only one of them is in this file.
//
//  ── GATES 1 AND 2 ARE THE WHOLE CONTRACT, AND THEY LIVE ELSEWHERE ─────────────────────────────
//
//  "Nothing about railRF's answers changes. Its existing tests are the gate, and if any number
//  moves, the promotion is wrong." That is asserted by tests/Ui.Tests/RailRf/ in its entirety and,
//  for the shipped example, by tests/Ui.Tests/Examples/PowerRailExampleTests — which parses the
//  numbers back out of examples/Power Rail/README.md, COMMITTED BEFORE THIS CHANGE, and compares
//  them to a live run. A file of numbers written before and a run performed after is exactly the
//  before/after comparison gate 1 asks for, and it is stronger than a summary because it covers
//  the whole page.
//
//  What is here is the part that is NEW: the naming mode, the broad phase, and the retained merge
//  edges.
//
//  ── AND ONE THING THE BRIEF ASKED FOR THAT THE WALK CANNOT PRODUCE ───────────────────────────
//
//  Gate 9 asks for a same-layer join carrying a point inside the intersection. There is no such
//  join to produce: two pieces of metal meeting on one drawing layer are unioned into a single
//  connected component by DrcRegions.Components BEFORE the union-find sees them, so every edge
//  DrcConnectivity retains today is a Via. The classification is by MEASUREMENT rather than by
//  which loop produced the union (see JoinOf), so it stays right for brief 9's boundary stitching;
//  what is gated here is the half that exists.
// ================================================================

using System.Linq;
using System.Text.RegularExpressions;
using Clipper2Lib;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Theming;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class SharedExtractionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-lvs2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey In1 = new(2, 0);
    private static readonly LayerKey In2 = new(3, 0);
    private static readonly LayerKey Bot = new(4, 0);
    private static readonly LayerKey ViaL = new(9, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // ══ R-lvs2-2: the naming mode ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 3 — <c>ArtworkOnly</c> ignores a schematic that would otherwise answer.</b>
    /// </summary>
    /// <remarks>
    /// This is LVS overview §1a made a test. The same board, the same pads, the same copper — which
    /// states no net at all — read twice. railRF's mode takes the schematic's binding; LVS's mode
    /// has nothing to take and says so, which is the only honest answer about artwork that names
    /// nothing. The failure this prevents has no symptom: LVS would ask the artwork what the
    /// artwork says, get the SCHEMATIC's answer back, and pass every design.
    /// </remarks>
    [Fact]
    public void ArtworkOnlyIgnoresASchematicThatWouldOtherwiseAnswer()
    {
        var fx = OnePartBoard();

        var railRf = PlacedPins.Of(
            fx.View, fx.Clay, fx.Tech, PinNaming.SchematicThenArtwork,
            _ => ["1", "2"], null, _ => ["VDD", "GND"]);

        Assert.Equal("VDD", Assert.Single(railRf, p => p.Pin == "1").Net);

        var lvs = PlacedPins.Of(fx.View, fx.Clay, fx.Tech, PinNaming.ArtworkOnly);

        Assert.Equal(railRf.Count, lvs.Count);
        Assert.All(lvs, p => Assert.Null(p.Net));
        Assert.All(lvs, p => Assert.Equal(PinSource.Artwork, p.Source));
    }

    /// <summary><b>Gate 4 — R-lvs2-2c.</b> Supplying a schematic-facing delegate in
    /// <c>ArtworkOnly</c> throws, rather than being quietly ignored.</summary>
    [Fact]
    public void SupplyingASchematicDelegateInArtworkOnlyThrows()
    {
        var fx = OnePartBoard();

        foreach (var call in new Action[]
        {
            () => PlacedPins.Of(fx.View, fx.Clay, fx.Tech, PinNaming.ArtworkOnly, _ => ["1"]),
            () => PlacedPins.Of(fx.View, fx.Clay, fx.Tech, PinNaming.ArtworkOnly, null, null, _ => ["VDD"]),
        })
        {
            var ex = Assert.Throws<ArgumentException>(call);
            output.WriteLine(ex.Message);
            Assert.Contains("ArtworkOnly", ex.Message, StringComparison.Ordinal);
        }
    }

    // ══ R-lvs2-3: the broad phase ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5 — the indexed answer equals the scan's, for every query.</b>
    /// </summary>
    /// <remarks>
    /// The only way to know a broad phase is not rejecting real hits. 2,000 randomised pieces on
    /// four layers, 10,000 randomised query points, asked both on-layer and any-layer — and the
    /// scan it is compared against is the one <c>PdnCopperPieces.PieceAt</c> performed before this
    /// brief, written out here rather than kept alive in the product.
    /// </remarks>
    [Fact]
    public void IndexAndScanAgreeOverRandomisedPieces()
    {
        var pieces = RandomPieces(2_000, seed: 20260921);
        var index = new PieceIndex(pieces);
        var rng = new Random(7);

        var layers = new LayerKey?[] { null, Top, In1, In2, Bot };
        for (int q = 0; q < 10_000; q++)
        {
            long x = rng.NextInt64(-Mm(1), Mm(51));
            long y = rng.NextInt64(-Mm(1), Mm(51));
            var layer = layers[q % layers.Length];

            Assert.Equal(Scan(pieces, x, y, layer), index.PieceAt(x, y, layer));
        }
    }

    /// <summary>
    /// <b>Gate 6 — the index is asymptotically better, asserted as a COUNTER.</b>
    /// </summary>
    /// <remarks>
    /// Never a wall clock (LVS overview §1i). The structural property is that a query tests a
    /// bounded number of pieces exactly, whatever the partition's size — so the partition grows
    /// 10x at the same piece size and spread, and the worst query's exact-test count must not.
    /// </remarks>
    [Fact]
    public void ExactTestsPerQueryStayBoundedAsThePartitionGrows()
    {
        int Worst(int count)
        {
            var pieces = RandomPieces(count, seed: 11, extentMm: Math.Sqrt(count) * 1.2);
            var index = new PieceIndex(pieces);
            var rng = new Random(3);
            int worst = 0;
            for (int q = 0; q < 2_000; q++)
            {
                index.PieceAt(
                    rng.NextInt64(0, Mm(Math.Sqrt(count) * 1.2)),
                    rng.NextInt64(0, Mm(Math.Sqrt(count) * 1.2)), null);
                worst = Math.Max(worst, index.Counters.ExactTests);
            }
            output.WriteLine($"{count,6} pieces → worst {worst} exact tests per query");
            return worst;
        }

        int small = Worst(200), large = Worst(2_000);

        // A linear scan would be 10x. The grid's candidate set is set by the CELL, not by the
        // partition, so the two numbers are the same order — the margin is generous because what is
        // being asserted is "bounded", not a particular constant.
        Assert.True(large <= small + 4, $"worst exact tests grew from {small} to {large}");
    }

    /// <summary>
    /// <b>Gate 7 — R-lvs2-3b: a board-wide pour does not defeat the grid.</b>
    /// </summary>
    /// <remarks>
    /// A pour's bounding box covers every cell, so it is a candidate for every query. That is fine
    /// BECAUSE THE NUMBER OF SUCH PIECES IS SMALL — and what the index must not do is smear it
    /// across every bucket, or test every small piece because of it. Exact tests per query stay
    /// within a small constant of the pour count.
    /// </remarks>
    [Fact]
    public void ABoardWidePourDoesNotDefeatTheGrid()
    {
        var pieces = new List<DrcNetPiece>(RandomPieces(2_000, seed: 5));

        // LAST, deliberately: candidates are tested in ascending piece order, so a pour at index 0
        // would answer every query on its first exact test and the assertion would prove nothing.
        pieces.Add(Piece(Bot, -Mm(1), -Mm(1), Mm(51), Mm(51), pieces.Count));

        var index = new PieceIndex(pieces);
        Assert.Equal(1, index.SpanningPieces);

        var rng = new Random(9);
        int worst = 0;
        for (int q = 0; q < 5_000; q++)
        {
            index.PieceAt(rng.NextInt64(0, Mm(50)), rng.NextInt64(0, Mm(50)), null);
            worst = Math.Max(worst, index.Counters.ExactTests);
        }

        output.WriteLine($"worst {worst} exact tests per query against 2,000 pieces and one pour");
        Assert.True(worst <= index.SpanningPieces + 4, $"worst exact tests was {worst}");
    }

    // ══ R-lvs2-4: the union-find remembers why ══════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 8 — the joins reconstruct the partition, exactly.</b>
    /// </summary>
    /// <remarks>
    /// Unioned independently, the retained edges must produce the same equivalence classes
    /// <c>DrcConnectivity</c>'s own net numbering does. A spanning forest that does not reproduce
    /// the partition is a wrong forest — and the way it would be wrong is by dropping an edge,
    /// which reads as an extra island and is precisely the failure that already cost this
    /// repository a four-layer inner plane.
    /// </remarks>
    [Fact]
    public void JoinsReconstructThePartition()
    {
        var pieces = DrcConnectivity.Extract(FourLayerBoard(), FourLayerTech(), out var joins);

        Assert.NotEmpty(joins);
        output.WriteLine($"{pieces.Count} pieces, {joins.Count} joins");

        var parent = Enumerable.Range(0, pieces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        foreach (var j in joins)
        {
            Assert.InRange(j.PieceA, 0, pieces.Count - 1);
            Assert.InRange(j.PieceB, 0, pieces.Count - 1);
            parent[Find(j.PieceA)] = Find(j.PieceB);
        }

        // A spanning FOREST: one edge per union, so never more than n-1 of them.
        Assert.True(joins.Count <= pieces.Count - 1, $"{joins.Count} joins over {pieces.Count} pieces");

        for (int a = 0; a < pieces.Count; a++)
            for (int b = a + 1; b < pieces.Count; b++)
                Assert.Equal(pieces[a].Net == pieces[b].Net, Find(a) == Find(b));
    }

    /// <summary>
    /// <b>Gate 9 — a via join carries the via's own coordinate.</b>
    /// </summary>
    /// <remarks>
    /// The coordinate is what brief 8 puts a marker on, so it has to be somewhere a user would
    /// look. <b>The same-layer half of this gate is not asserted because the walk cannot produce
    /// one</b> — see this file's header.
    /// </remarks>
    [Fact]
    public void AViaJoinCarriesTheViasOwnCoordinate()
    {
        DrcConnectivity.Extract(FourLayerBoard(), FourLayerTech(), out var joins);

        Assert.All(joins, j =>
        {
            Assert.Equal(JoinKind.Via, j.Kind);
            Assert.Equal(ViaL, j.Layer);
            Assert.Equal(Mm(5), j.X);
            Assert.Equal(Mm(5), j.Y);
        });
    }

    /// <summary>
    /// <b>Gate 10 — the four-layer inner-plane case still works.</b>
    /// </summary>
    /// <remarks>
    /// The one that was learned the hard way (<c>src/Design/RESOLVED.md</c>, 2026-09-19): a plated
    /// barrel shorts every conductor it passes, and joining only the span's two ends read a power
    /// plane on an inner layer as a galvanically separate island — carrying no current,
    /// contributing nothing, with the picture showing it plainly connected. It could not fail
    /// loudly either, because an extra island is what railRF calls ORDINARY on imported artwork.
    /// </remarks>
    [Fact]
    public void TheFourLayerInnerPlaneCaseStillWorks()
    {
        var pieces = DrcConnectivity.Extract(FourLayerBoard(), FourLayerTech());

        foreach (var layer in new[] { Top, In1, In2, Bot, ViaL })
            Assert.Contains(pieces, p => p.Layer == layer);

        Assert.Single(pieces.Select(p => p.Net).Distinct());
    }

    // ══ R-lvs2-1d / R-lvs2-5a: no old name survives ═════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 11 — the old names do not survive as aliases.</b>
    /// </summary>
    /// <remarks>
    /// A type with two names is two types as far as a later reader is concerned, and this
    /// repository has already paid for that with three copies of a version number. Comment-stripped,
    /// because the promoted files' own prose names what they used to be called — and a scan that a
    /// comment could defeat is a scan that would have to be silenced the first time somebody
    /// documented the move.
    /// </remarks>
    [Fact]
    public void NoOldTypeNameSurvivesAnywhereUnderSrc()
    {
        string[] forbidden =
            ["PdnCopperPieces", "PdnPadSource", "PdnPadSummary", "PdnLayoutPads", "BuildLayerRegions"];

        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
             || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            string source = StripComments(File.ReadAllText(file));

            foreach (string name in forbidden)
                if (source.Contains(name, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {name}");

            if (Regex.IsMatch(source, @"\bPdnPad\b")) offenders.Add($"{Path.GetFileName(file)}: PdnPad");
        }

        Assert.Empty(offenders);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private sealed record Fx(LayoutView View, string Clay, Technology Tech);

    /// <summary>One two-pin part placed on a board whose copper states NO net at all — so the only
    /// thing that can answer is the schematic, which is what gate 3 needs.</summary>
    private Fx OnePartBoard()
    {
        var tech = FourLayerTech();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "Board.ctech"), tech);
        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"),
            new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        string landDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "C0402"), ViewType.Layout);
        Directory.CreateDirectory(landDir);
        var land = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pin, x) in new[] { ("1", -Um(400)), ("2", Um(400)) })
        {
            land.Pins.Add(new LayoutPin { Name = pin, X = x, Y = 0, WidthDbu = Um(250), Layer = Top });
            land.Shapes.Add(new RectShape
            {
                Layer = Top, Pin = pin,
                X1 = x - Um(125), Y1 = -Um(125), X2 = x + Um(125), Y2 = Um(125),
            });
        }
        LayoutPersistence.SaveToFile(Path.Combine(landDir, "C0402.clay"), land);

        string cellDir = CellFolder.CreateCellFolder(_root, "Board");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "C0402"),
            X = Mm(5), Y = Mm(5), Mag = 1.0, RefDes = "C1", SchematicId = "C1",
        });

        string clay = Path.Combine(layoutDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return new Fx(view, clay, tech);
    }

    /// <summary>Four conductors, one through via from <c>TOP</c> to <c>BOT</c>.</summary>
    private static Technology FourLayerTech()
    {
        var tech = new Technology { Name = "FourLayer" };
        tech.Layers =
        [
            new LayerDef { Key = Top,  Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = In1,  Name = "IN1", ZOrder = 1, Color = new Rgba(90, 160, 90, 255) },
            new LayerDef { Key = In2,  Name = "IN2", ZOrder = 2, Color = new Rgba(160, 160, 60, 255) },
            new LayerDef { Key = Bot,  Name = "BOT", ZOrder = 3, Color = new Rgba(40, 90, 200, 255) },
            new LayerDef { Key = ViaL, Name = "VIA", ZOrder = 4, Color = new Rgba(120, 120, 120, 255) },
        ];

        StackupLayer Cu(string name, LayerKey key) => new()
        {
            Kind = StackupKind.Conductor, Name = name,
            ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [key],
        };
        StackupLayer Pp() => new()
        {
            Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02,
        };

        tech.Stackup.Layers =
        [
            Cu("TOP", Top),
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaL],
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
            Pp(), Cu("IN1", In1), Pp(), Cu("IN2", In2), Pp(), Cu("BOT", Bot),
        ];
        return tech;
    }

    /// <summary>A pad on each of the four conductors and ONE barrel through all of them. The inner
    /// two are the planes that used to come back as separate islands.</summary>
    private static Dictionary<LayerKey, Paths64> FourLayerBoard()
    {
        var shapes = new List<LayoutShape>();
        foreach (var layer in new[] { Top, In1, In2, Bot })
            shapes.Add(new RectShape
            {
                Layer = layer,
                X1 = Mm(5) - Um(300), Y1 = Mm(5) - Um(300),
                X2 = Mm(5) + Um(300), Y2 = Mm(5) + Um(300),
            });

        shapes.Add(new RectShape
        {
            Layer = ViaL,
            X1 = Mm(5) - Um(100), Y1 = Mm(5) - Um(100),
            X2 = Mm(5) + Um(100), Y2 = Mm(5) + Um(100),
        });

        return LayerRegions.Build(shapes, FourLayerTech());
    }

    // ── the broad phase's own fixtures ──────────────────────────────────────────────────────────

    private static DrcNetPiece Piece(LayerKey layer, long x1, long y1, long x2, long y2, int net)
    {
        Paths64 paths = [[new Point64(x1, y1), new Point64(x2, y1), new Point64(x2, y2), new Point64(x1, y2)]];
        return new DrcNetPiece(layer, paths, new Bbox(x1, y1, x2, y2), net);
    }

    /// <summary>Disjoint squares scattered over a square board, on four layers — the shape a real
    /// partition has, minus the pours, which gate 7 adds on purpose.</summary>
    private static IReadOnlyList<DrcNetPiece> RandomPieces(int count, int seed, double extentMm = 50)
    {
        var rng = new Random(seed);
        var layers = new[] { Top, In1, In2, Bot };
        var pieces = new List<DrcNetPiece>(count);

        for (int i = 0; i < count; i++)
        {
            long x = (long)(rng.NextDouble() * Mm(extentMm));
            long y = (long)(rng.NextDouble() * Mm(extentMm));
            long w = Um(100 + rng.Next(400));
            pieces.Add(Piece(layers[i % layers.Length], x, y, x + w, y + w, i));
        }
        return pieces;
    }

    /// <summary>The linear scan <c>PieceAt</c> was, written out so the index can be compared with
    /// it rather than with itself.</summary>
    private static int Scan(IReadOnlyList<DrcNetPiece> pieces, long x, long y, LayerKey? layer)
    {
        foreach (var piece in pieces)
        {
            if (layer is { } only && piece.Layer != only) continue;
            if (!piece.Bounds.Contains(x, y)) continue;
            if (Regions.Contains(piece.Paths, x, y)) return piece.Net;
        }
        return -1;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }
}
