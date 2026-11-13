// brief-dfr22-2-artwork-schematic-drawing.md §4 — Create Schematic from Artwork draws "As on the board": each part
// where the board has it (neighbours keep their left/right and above/below order, nothing overlaps, the circuit is
// Tidy's), while Tidy stays byte for byte what it was. A bias chain hanging off the main line draws with no wire
// looping back (R-3), every label sits on a wire of its own net (R-4), a coplanar T is the microstrip tee (R-6), and
// Create shows progress, can be cancelled with nothing written (R-7) and reuses the preview's recognition (R-8).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using CircuitRF.Ui.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.LineBoards;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ArtworkBoardArrangementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-dfr22-2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private RecognitionRun Draw(RecognitionInput input, ArtworkArrange arrange) =>
        ArtworkRecognition.Draw(input, Path.Combine(_root, "Board_model", "schematic"),
            new RecognitionRunOptions
            {
                Now = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), Emit = new RecognitionEmitOptions { Arrange = arrange },
            });

    /// <summary>Each net's name and the pins on it, as Simulate extracts them.</summary>
    private static string Partition(SchematicEditModel m) =>
        string.Join("\n", NetExtractor.Extract(m).TestBench.Instances
            .SelectMany(i => i.NetBindings.Select((n, k) => (Net: n, Pin: $"{i.InstanceName}.{k}")))
            .Where(p => !p.Pin.StartsWith("GND", StringComparison.Ordinal))
            .GroupBy(p => p.Net).Select(g => $"{g.Key}: {string.Join(" ", g.Select(p => p.Pin).Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal));

    private static Dictionary<string, SchematicComponent> Glyphs(SchematicEditModel m) =>
        m.BuildRenderModel().Model.Components.Where(c => c.InstanceName.Length > 0).ToDictionary(c => c.InstanceName);

    [Fact]
    public void AsOnTheBoard_KeepsNeighboursInTheBoardsOrder_OverlapsNothing_AndIsTidysCircuit()
    {
        var input = EmitBoards.Saved(_root);
        var board = Draw(input, ArtworkArrange.Board);
        var tidy = Draw(input, ArtworkArrange.Tidy);
        var m = Assert.IsType<SchematicEditModel>(board.Schematic);

        Assert.Equal(0, board.Report.Count(RecognitionFindingClass.BoardPlacementFallbacks));
        Assert.Equal(Partition(tidy.Schematic!), Partition(m));

        var glyphs = Glyphs(m);
        var parts = m.Components.Where(c => c.FromArtwork).ToList();
        (double X, double Y) Centre(EditableComponent c) =>
            ((glyphs[c.InstanceName].GlyphBbMinX + glyphs[c.InstanceName].GlyphBbMaxX) / 2,
             (glyphs[c.InstanceName].GlyphBbMinY + glyphs[c.InstanceName].GlyphBbMaxY) / 2);
        (double X, double Y) Board(EditableComponent c) => ArtworkAnchors.Centre(c.ArtworkAnchor)!.Value;

        // Neighbours — two instances sharing a net — keep the board's order along the axis they are apart on.
        var instances = board.Circuit!.TestBench.Instances.ToDictionary(i => i.InstanceName);
        int checkedPairs = 0;
        for (int i = 0; i < parts.Count; i++)
            for (int j = i + 1; j < parts.Count; j++)
            {
                var (a, b) = (parts[i], parts[j]);
                if (!instances[a.InstanceName].NetBindings.Where(n => n != "0").Intersect(instances[b.InstanceName].NetBindings).Any()) continue;
                var (ba, bb) = (Board(a), Board(b));
                var (sa, sb) = (Centre(a), Centre(b));
                if (Math.Abs(bb.X - ba.X) >= Math.Abs(bb.Y - ba.Y))
                    Assert.True(Math.Sign(bb.X - ba.X) == Math.Sign(sb.X - sa.X), $"{a.InstanceName}/{b.InstanceName} left/right");
                else
                    Assert.True(Math.Sign(bb.Y - ba.Y) == -Math.Sign(sb.Y - sa.Y), $"{a.InstanceName}/{b.InstanceName} above/below");
                checkedPairs++;
            }
        Assert.True(checkedPairs >= 8, $"only {checkedPairs} neighbour pairs");

        // No two glyphs overlap (a quarter cell inside each edge: drawn touching is not on top).
        var boxes = parts.Select(c => glyphs[c.InstanceName]).Select(g => (L: g.GlyphBbMinX + 25, T: g.GlyphBbMinY + 25, R: g.GlyphBbMaxX - 25, B: g.GlyphBbMaxY - 25)).ToList();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                Assert.False(boxes[i].L < boxes[j].R && boxes[j].L < boxes[i].R && boxes[i].T < boxes[j].B && boxes[j].T < boxes[i].B,
                             $"{parts[i].InstanceName} overlaps {parts[j].InstanceName}");
    }

    /// <summary>SHA-256 of the synthetic board's Tidy drawing (provenance block aside) as it was drawn before "As on the
    /// board" existed, every number of ten or more decimals taken to 12 significant digits first (<see cref="LastBitsAside"/>).</summary>
    private const string TidyBefore = "672cc2e5da3685826c4f28e1196292fba1c6d3da4d4084de9277778d8b854e36";

    [Fact]
    public void Tidy_IsByteIdenticalToBefore()
    {
        var m = Draw(EmitBoards.Saved(_root), ArtworkArrange.Tidy).Schematic!;
        m.ArtworkSource = null;
        Assert.Equal(TidyBefore, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LastBitsAside(SchematicPersistence.Serialize(m))))));
    }

    /// <summary>The lines' computed Eeff and Z0 differ in their last bits between platforms (macOS 3.197740411708634,
    /// Linux arm64 3.197740411708622: libm), and nothing else in the drawing does. Rounded to 12 significant digits they
    /// agree, and a drawing that moved anything still changes the hash.</summary>
    private static string LastBitsAside(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, @"-?\d+\.\d{10,}(?:[eE][-+]?\d+)?",
            x => double.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture)
                       .ToString("G12", System.Globalization.CultureInfo.InvariantCulture));

    // ── R-3: the loop fixture ───────────────────────────────────────────────────────────────────────

    /// <summary>Two series lines and a shunt port to ground hanging off a junction of the main path, with a shunt
    /// capacitor on the link between them — listed BEFORE the chain, the order that put the chain beside it.</summary>
    private const string BiasChain = """
        Port:P1 rfin 0 Num=1 Z=50 Ohm
        C:C1 rfin n1 C=10 pF
        MLIN:TL1 n1 n2 W=0.5 mm L=5 mm
        MLIN:TL2 n2 n3 W=0.5 mm L=5 mm
        MLIN:TL3 n3 rfout W=0.5 mm L=5 mm
        Port:P2 rfout 0 Num=2 Z=50 Ohm
        C:C2 n4 0 C=100 pF
        MLIN:TL4 n2 n4 W=0.3 mm L=4 mm
        MLIN:TL5 n4 vb W=0.3 mm L=4 mm
        Port:P3 vb 0 Num=3 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz
        """;

    /// <summary>The chain on a board, mm, y up: the main line across at y = 10, the chain straight down from TL1/TL2's
    /// junction, C2 beside it.</summary>
    private static Dictionary<string, BoardHint> ChainBoard()
    {
        static BoardHint At(double x, double y, double? deg = null) => new((long)(x * 1e6), (long)(y * 1e6), deg);
        return new()
        {
            ["P1"] = At(0, 10), ["C1"] = At(2, 10, 0), ["TL1"] = At(6, 10, 0), ["TL2"] = At(12, 10, 0), ["TL3"] = At(18, 10, 0),
            ["P2"] = At(22, 10), ["TL4"] = At(9, 7, -90), ["TL5"] = At(9, 2.5, -90), ["P3"] = At(9, 0), ["C2"] = At(11, 4.75, -90),
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABiasChain_IsDrawnWithNoWireDoublingBack(bool onBoard)
    {
        var (lib, tb) = new CnlReader().Read(BiasChain);
        var exact = onBoard ? NetlistSchematic.BoardPlacement(tb, ChainBoard()) : null;
        var r = NetlistSchematic.Build(lib, tb, exact: exact);
        var m = Assert.IsType<SchematicEditModel>(r.Schematic);
        Assert.Empty(r.PlacementFallbacks);

        var glyphs = Glyphs(m).Values.Where(g => !g.InstanceName.StartsWith("GND", StringComparison.Ordinal)).ToList();
        foreach (var w in m.Wires)
        {
            var p = w.Points;
            for (int i = 0; i + 3 < p.Count; i++)
            {
                // Segments i and i+2 running opposite ways is a wire turning back on itself round something.
                double ax = p[i + 1].X - p[i].X, ay = p[i + 1].Y - p[i].Y, bx = p[i + 3].X - p[i + 2].X, by = p[i + 3].Y - p[i + 2].Y;
                Assert.False(ax * bx + ay * by < 0, $"a wire doubles back: {string.Join(" ", p)}");
            }
            for (int i = 0; i + 1 < p.Count; i++)
                foreach (var g in glyphs)
                {
                    double mx = (p[i].X + p[i + 1].X) / 2, my = (p[i].Y + p[i + 1].Y) / 2;
                    Assert.False(mx > g.GlyphBbMinX + 25 && mx < g.GlyphBbMaxX - 25 && my > g.GlyphBbMinY + 25 && my < g.GlyphBbMaxY - 25,
                                 $"a wire runs through {g.InstanceName}");
                }
        }
        Assert.Equal(["n4", "vb"], NetExtractor.Extract(m).TestBench.Instances.Single(i => i.InstanceName == "TL5").NetBindings);
    }

    // ── R-4: labels ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryLabel_IsOnAWireOfItsOwnNet_OrOnTheTerminalOfANetWithNoWire()
    {
        var input = EmitBoards.Saved(_root);
        var (lib, tb) = new CnlReader().Read(BiasChain);
        var drawings = new[]
        {
            Draw(input, ArtworkArrange.Board).Schematic!, Draw(input, ArtworkArrange.Tidy).Schematic!,
            NetlistSchematic.Build(lib, tb).Schematic!, NetlistSchematic.Build(lib, tb, exact: NetlistSchematic.BoardPlacement(tb, ChainBoard())).Schematic!,
        };
        foreach (var drawn in drawings)
        {
            // Written and read back: what is checked is what the file holds.
            var (m, _, _) = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(drawn));
            var render = m.BuildRenderModel().Model.Components.ToDictionary(c => c.Id);
            var pins = m.Components.SelectMany(c => render[c.Id].Ports.Select(p =>
                SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, c.X, c.Y, c.Rotation, c.MirrorX))).ToHashSet();
            foreach (var label in m.NetLabels)
            {
                if (!label.IsAnchored)
                {
                    Assert.Contains((label.X, label.Y), pins);
                    continue;
                }
                var wire = Assert.IsType<EditableWire>(m.FindWire(label.OwnerWireId));
                double gap = Enumerable.Range(0, wire.Points.Count - 1).Min(i => Distance(label.X, label.Y, wire.Points[i], wire.Points[i + 1]));
                Assert.True(gap <= SchematicComponent.LabelWorldHeight, $"'{label.Name}' is {gap} from its wire");
            }
            // And each names its own net: the drawing extracts to every net name the circuit states.
            var names = NetExtractor.Extract(m).TestBench.Instances.SelectMany(i => i.NetBindings).ToHashSet();
            Assert.All(m.NetLabels, l => Assert.Contains(l.Name, names));
        }
    }

    private static double Distance(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len, 0, 1);
        return Math.Sqrt(Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2));
    }

    // ── R-6: a coplanar T ───────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="LineBoards.Junction"/> with a top pour 500 µm clear of the T on every side, stitched.</summary>
    private static CircuitRF.Design.Layout.LayoutView CoplanarT()
    {
        const double g = 500, h = W / 2;
        var view = Junction(cross: false);
        view.Shapes.Add(Rect(Top, 0, 10_000 + h + g, 30_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 0, 15_000 - h - g, 10_000 - h - g));
        view.Shapes.Add(Rect(Top, 15_000 + h + g, 0, 30_000, 10_000 - h - g));
        foreach (var (x, y) in new[] { (3_000.0, 17_000.0), (27_000.0, 17_000.0), (3_000.0, 3_000.0), (27_000.0, 3_000.0) })
            view.Shapes.Add(ViaAt(x, y));
        return view;
    }

    [Fact]
    public void ACoplanarT_IsTheMicrostripTeeWithItsArmsWidths_AndSaysSo()
    {
        var result = Recognize(CoplanarT(), TwoLayer());

        var tee = Assert.Single(result.Lines.OfType(LineElementType.MTEE));
        Assert.All(new[] { "W1", "W2", "W3" }, w => Assert.Equal(W, tee.Micro(w), 1.0));
        var arms = tee.Nodes.Select(n => Assert.Single(Lines(result), e => e.Nodes.Contains(n))).ToList();
        Assert.All(arms, a => Assert.Equal(LineElementType.CPWG, a.Type));
        // The reference planes are the microstrip tee's: the through arms stop half the branch's width short of the
        // centre, the branch half the through line's.
        Assert.Equal(15_000 - W / 2, arms[0].Micro("L"), 1.0);
        Assert.Equal(15_000 - W / 2, arms[1].Micro("L"), 1.0);
        Assert.Equal(10_000 - W / 2, arms[2].Micro("L"), 1.0);
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.CoplanarJunctionsAsMicrostrip));
        Assert.Contains("(no coplanar tee model)", result.Report.Of(RecognitionFindingClass.CoplanarJunctionsAsMicrostrip)!.Sentence);
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.DiscontinuitiesNotModelled));

        // A microstrip T says nothing of the kind.
        Assert.Equal(0, Recognize(Junction(cross: false), TwoLayer()).Report.Count(RecognitionFindingClass.CoplanarJunctionsAsMicrostrip));
    }

    // ── R-7, R-8: Create ────────────────────────────────────────────────────────────────────────────

    /// <summary>The real recognition, counted: every preview recognises, and so does a Create handed no recognition.</summary>
    private sealed class CountingRunner : IArtworkRecognitionRunner
    {
        public int Recognitions;
        public Action? OnRun;

        public RecognitionInput Load(string clayPath) => ArtworkRecognitionRunner.Instance.Load(clayPath);

        public (RecognitionResult Result, RecognitionCircuit? Circuit) Preview(RecognitionInput input, RecognitionEmitOptions emit, RunControl? control)
        {
            System.Threading.Interlocked.Increment(ref Recognitions);
            return ArtworkRecognitionRunner.Instance.Preview(input, emit, control);
        }

        public RecognitionRun Run(RecognitionInput input, RecognitionTarget target, RecognitionRunOptions options, RunControl? control)
        {
            if (options.Recognised is null) System.Threading.Interlocked.Increment(ref Recognitions);
            OnRun?.Invoke();
            return ArtworkRecognitionRunner.Instance.Run(input, target, options, control);
        }
    }

    private async Task<CreateSchematicFromArtworkViewModel> Open(CountingRunner runner)
    {
        var vm = new CreateSchematicFromArtworkViewModel(EmitBoards.Saved(_root), [], runner, debounce: TimeSpan.Zero);
        await vm.Recognition;
        return vm;
    }

    [Fact]
    public async Task PreviewThenCreate_RecognisesOnce()
    {
        var runner = new CountingRunner();
        var vm = await Open(runner);
        RecognitionRun? created = null;
        vm.Created += r => created = r;

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.True(created is { Ok: true }, vm.Status);
        Assert.Equal(1, runner.Recognitions);
        Assert.Equal(1, created!.Report.Count(RecognitionFindingClass.SchematicWritten));
        Assert.Single(created.Report.Findings, f => f.Class == RecognitionFindingClass.LineElements);
    }

    [Fact]
    public async Task ACancelledCreate_WritesNothing()
    {
        var runner = new CountingRunner();
        var vm = await Open(runner);
        runner.OnRun = () => vm.CancelRunCommand.Execute(null);   // Cancel pressed while it runs
        string cell = RecognitionTarget.NewCellDir(vm.ClayPath, vm.NewCellName);

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.False(vm.IsCreating);
        Assert.Contains("nothing was written", vm.Status);
        Assert.False(Directory.Exists(cell));
    }
}
