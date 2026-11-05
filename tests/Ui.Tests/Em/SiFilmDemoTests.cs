// TFR-V R-tfrv-7 — the shipped silicon demo, whose film sits between two metals: what connects, what crosses, and what the part draws.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class SiFilmDemoTests
{
    private const string Id = "rfic-Si_3LM_TFR_100um";
    private const int Dbu = 1000;
    private static readonly LayerKey M1 = new(1, 0), M2 = new(2, 0), M3 = new(3, 0), Film = new(4, 0), Hi = new(7, 0), Via12 = new(5, 0), Contact = new(11, 0);

    private static Technology Tech() => ShippedTechnologies.Load(Id);

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) => new()
    {
        Layer = layer, X1 = (long)(x1 * Dbu), Y1 = (long)(y1 * Dbu), X2 = (long)(x2 * Dbu), Y2 = (long)(y2 * Dbu),
    };

    private static LabelShape Port(string name, double x) => new()
    {
        Layer = M2, X = (long)(x * Dbu), Y = 0, Text = name, Height = 5000, IsPort = true,
    };

    /// <summary>180 µm of a 40 µm film between the vias' inner edges, with heads on Metal2.</summary>
    private static List<LayoutShape> Resistor(params LayoutShape[] more) =>
    [
        Rect(M2, 0, -25, 100, 25), Rect(M2, 220, -25, 320, 25),
        Rect(Film, 50, -20, 270, 20),
        Rect(Contact, 60, -15, 70, 15), Rect(Contact, 250, -15, 260, 15),
        Port("1", 0), Port("2", 320), .. more,
    ];

    private static double DcSeriesR(IEnumerable<LayoutShape> art)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(art);
        string scratch = Path.Combine(Path.GetTempPath(), "crf-sidemo-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var setup = new EmSetup
            {
                Name = "dc", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
                AnalysisLevelNames = ["Metal1", "TFR Level", "Metal2"],
                Frequency = new FrequencySpec("0", "0", 1),
            };
            var run = EmRunService.Run(setup, new EmLayoutSource(Path.Combine(scratch, "a.clay"), view, Tech(), Dbu), scratch);
            Assert.True(run.Status == EmRunStatus.Ok, run.Error);
            Complex s21 = run.Data!.Cubes["S"].ComplexValues[2];
            return 100.0 * (1.0 / s21.Real - 1.0);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static CopperPieces Pieces(params LayoutShape[] art) => CopperPieces.Build(art, Tech());

    private static int NetAt(CopperPieces p, double x, double y, LayerKey layer)
    {
        int piece = p.IndexAt((long)(x * Dbu), (long)(y * Dbu), layer);
        Assert.True(piece >= 0, $"nothing on {layer} at ({x}, {y})");
        return p.NetOfPiece(piece);
    }

    // ── Gate 9 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDemoValidatesClean() => Assert.Empty(TechValidation.Analyze(Tech()));

    /// <summary>The contact is on Metal2, which is above the film, and not on the base metal.</summary>
    [Fact]
    public void TheStackFindsTheContactMetal()
    {
        var stack = MmicStackResolver.Resolve(Tech());
        Assert.Equal("Metal2", stack.ResistorContactMetal?.Name);
        Assert.Equal("TFR Contact", stack.ResistorContactVia?.Name);
        Assert.Equal("Metal1", stack.BaseMetal?.Name);
        Assert.Equal("Metal2", stack.BridgeMetal?.Name);
        Assert.Equal(["Resistor", "Resistor Hi"], MmicStackResolver.FilmNames(Tech()));
    }

    /// <summary>The part's artwork keeps the demo's own enclosure rules, on either film, at any segment count.</summary>
    [Theory]
    [InlineData("Resistor", 1)]
    [InlineData("Resistor Hi", 1)]
    [InlineData("Resistor", 3)]
    [InlineData("Resistor Hi", 4)]
    public void ThePartPassesTheDemosRules(string film, int n)
    {
        var art = TfrPCell.Generate(new Dictionary<string, PCellValue>
        {
            ["Film"] = PCellValue.Text(film), ["N"] = PCellValue.Real(n), ["W"] = PCellValue.Real(10e-6), ["L"] = PCellValue.Real(40e-6), ["Lc"] = PCellValue.Real(10e-6),
        }, Tech(), PCellLayerSelection.Default);
        Assert.Contains(art.Shapes, s => s.Layer == Contact);
        Assert.All(art.Pins, p => Assert.Equal(M2, p.Layer));
        Assert.Empty(DrcEngine.Run([.. art.Shapes], Tech()).Violations);
    }

    /// <summary>A Metal3 line with no film drawn reads as it does on the same stack and total height without the band: gate 1 on this
    /// stack. The committed pair is testdata/em-tfr/si-demo.</summary>
    [Fact]
    public void AMetal3LineReadsAsALine()
    {
        string refs = Path.Combine(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "si-demo", "refs");
        static Dictionary<double, double[]> Read(string path) => File.ReadLines(path)
            .Where(l => l.Length > 0 && l[0] is not ('!' or '#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
            .ToDictionary(v => v[0], v => v[1..]);
        var with = Read(Path.Combine(refs, "p_line.s2p"));
        var without = Read(Path.Combine(refs, "p_line_noband.s2p"));
        Assert.Equal(3, with.Count);
        foreach (double f in with.Keys)
            for (int e = 0; e < 8; e += 2)
                Assert.InRange(new Complex(with[f][e] - without[f][e], with[f][e + 1] - without[f][e + 1]).Magnitude, 0, 0.002);
    }

    /// <summary>The shipped example carries a byte-identical copy of this technology.</summary>
    [Fact]
    public void TheExamplesTechnologyIsTheShippedOneByteForByte()
    {
        string root = CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot();
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "src", "Design", "resources", "technologies", Id + ".ctech")),
                     File.ReadAllBytes(Path.Combine(root, "examples", "Via Contact Resistor", "tech", Id + ".ctech")));
    }

    // ── Gate 10 ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>TFR Contact joins Metal2 and stops: Metal1 under it stays on a net of its own.</summary>
    [Fact]
    public void TheContactDoesNotReachMetal1()
    {
        var p = Pieces(Rect(M2, 0, -25, 100, 25), Rect(Contact, 60, -15, 70, 15), Rect(M1, 55, -30, 75, 30));
        Assert.Equal(NetAt(p, 65, 0, M2), NetAt(p, 65, 0, Contact));
        Assert.NotEqual(NetAt(p, 65, 0, M2), NetAt(p, 65, 28, M1));
    }

    /// <summary>Via12 passes the film's z and joins Metal1 to Metal2. A film there is not on its net, and is a DRC error.</summary>
    [Fact]
    public void Via12JoinsItsMetalsAndAFilmInItsPathIsAnError()
    {
        var art = new LayoutShape[] { Rect(M1, 0, 0, 20, 20), Rect(M2, 0, 0, 20, 20), Rect(Via12, 5, 5, 15, 15) };
        var p = Pieces(art);
        Assert.Equal(NetAt(p, 10, 10, M1), NetAt(p, 10, 10, M2));
        Assert.Empty(DrcEngine.Run([.. art], Tech()).Violations);

        var withFilm = DrcEngine.Run([.. art, Rect(Film, 0, 0, 20, 20)], Tech()).Violations;
        Assert.Contains(withFilm, v => v.RuleName == "Via12 clear of films");
    }

    /// <summary>Metal1 under the film and Metal2 over it, on a run with all three levels, read as metal beside it does: nothing
    /// is contacted. The reference is metal beside rather than none, because drawing anything else on the stack moves this short
    /// film's mesh, and its DC reading, by 3.3 % wherever the metal is (src/Design/RESOLVED.md, TFR-V).</summary>
    [Fact]
    public void MetalOverAndUnderTheFilmLeavesItsResistanceAlone()
    {
        double beside = DcSeriesR(Resistor(Rect(M2, 150, 30, 170, 70)));
        double crossed = DcSeriesR(Resistor(Rect(M1, 150, -40, 170, 40), Rect(M2, 150, -40, 170, 40)));
        Assert.InRange(crossed / beside - 1, -0.01, 0.01);
    }
}
