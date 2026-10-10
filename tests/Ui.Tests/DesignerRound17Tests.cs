// Designer report, round 17 — a four-layer Gerber set whose Technology list offered no four-layer stackup, and a
// recognised trace left open beside the pad it leaves. One test per claim:
//   * copper closer than a micron is one piece; a real gap is still a gap;
//   * a line across such a gap reaches what is beyond it, and the join is reported;
//   * a numbered-metal set (MET-1 … MET-4) is four copper layers, top to bottom;
//   * a rout-only drill file imported INTO a technology is not refused for wanting a via layer;
//   * a copper count answered in the table enables the stackup it makes up;
//   * files answered copper at one position stack by the numbers in their names.

using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Diagnostics.Fixtures;
using CircuitRF.Ui.Views.Dialogs;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests;

public sealed class DesignerRound17Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-r17-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>0.46 µm is the field board's gap: a 0.375 mm pad on a 0.1 mil grid against a taper vertex rounded
    /// onto it. 2 µm is wider than any rounding and narrower than anything a PCB process draws.</summary>
    [Theory]
    [InlineData(0.46, 1)]
    [InlineData(2.0, 2)]
    public void CopperCloserThanAMicron_IsOnePiece_AndARealGapIsNot(double gapUm, int pieces)
    {
        LayoutShape[] copper = [Rect(Top, 0, 0, 500, 200), Rect(Top, 500 + gapUm, 0, 3_000, 200)];

        Assert.Equal(pieces, CopperPieces.Build(copper, TwoLayer()).Count);
    }

    [Fact]
    public void ALineAcrossAHairlineGap_ReachesThePortBeyondIt_AndTheJoinIsReported()
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 30_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 4_900, 500, 5_100));
        view.Shapes.Add(Rect(Top, 500.46, 4_900, 3_000, 5_100));
        view.Shapes.Add(new LabelShape { Layer = Top, X = 0, Y = Um(5_000), Text = "1", IsPort = true });
        view.Shapes.Add(new LabelShape { Layer = Top, X = Um(3_000), Y = Um(5_000), Text = "2", IsPort = true });
        var input = new RecognitionInput { View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes };

        var result = ArtworkRecognition.Recognize(input);

        Assert.True(result.Ok, result.Refusal);
        var nodes = result.Lines.Elements.SelectMany(e => e.Nodes).ToHashSet();
        Assert.Contains("P1", nodes);
        Assert.Contains("P2", nodes);
        Assert.DoesNotContain(nodes, n => n.StartsWith('O'));
        Assert.Contains(result.Report.Findings, f => f.Class == RecognitionFindingClass.HairlineGapsJoined);
    }

    [Fact]
    public void ANumberedMetalSet_IsFourCopperLayers_TopToBottom()
    {
        string dir = Folder("met");
        for (int n = 1; n <= 4; n++) File.WriteAllText(Path.Combine(dir, $"MET-{n}.PHO"), Artwork(n));

        var result = GerberImport.Import(FilesIn(dir), _root, "met", null, 1000);

        Assert.Equal(["Top Copper", "Inner 1", "Inner 2", "Bottom Copper"],
                     Enumerable.Range(1, 4).Select(n => result.Layers.Single(l => l.FileName == $"MET-{n}.PHO").LayerName));
    }

    /// <summary>The board outline as an Excellon ROUTE — tool down, four G01 moves, no hole. Into a technology it
    /// was refused with "map it to a via layer", which would have built a conductive wall round the board.</summary>
    [Fact]
    public void ARoutOnlyDrillFile_IntoATechnology_IsNotRefusedForWantingAViaLayer()
    {
        string tech = WorkspaceCreate.InstallTechnology(_root, DocGerberFixtures.SixLayerId);
        var files = DocGerberFixtures.WriteSixCopperSet(Folder("six")).ToList();
        string route = Path.Combine(_root, "six", "ROUTE.PHO");
        File.WriteAllText(route,
            "M48\nMETRIC\nT1C0.200000\n%\nG90\nT1\nG00X0.000000Y0.000000\nM15\n" +
            "G01X5.000000Y0.000000\nX5.000000Y5.000000\nX0.000000Y5.000000\nX0.000000Y0.000000\nM16\nM30\n");
        files.Add(route);

        var result = GerberImport.Import(files, _root, "board", null, 1000, target: GerberTechnologyTarget.Use(tech));

        Assert.Null(result.Refusal);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void ACopperCountAnsweredInTheTable_EnablesTheStackupItMakesUp()
    {
        string four = WorkspaceCreate.InstallTechnology(_root, DocGerberFixtures.FourLayerId);
        var row = GerberTechnologyChoices.Build(_root, copperCount: 0, [])
                                         .Single(c => c.Kind == GerberTechnologyChoiceKind.Workspace && c.Path == four);

        Assert.False(row.IsEnabled);
        Assert.True(row.EnabledFor(4));
    }

    /// <summary>Shape counts 1, 3, 2, 4 — the table lists rows by shape count, so the old order was 4, 2, 3, 1.</summary>
    [Fact]
    public void FilesAnsweredCopperAtOnePosition_StackByTheNumbersInTheirNames()
    {
        string dir = Folder("foil");
        int[] flashes = [1, 3, 2, 4];
        for (int n = 1; n <= 4; n++) File.WriteAllText(Path.Combine(dir, $"foil-{n}.art"), Artwork(n, flashes[n - 1]));

        var result = GerberImport.Import(FilesIn(dir), _root, "foil", null, 1000,
            resolveMapping: request => new GerberMappingAnswer(
                [.. request.Rows.Select(r => r.StackupConductors is null ? r
                    : r with { Stackup = new LayerStackupChoice(true, LayerStackupChoice.AboveTheTop) })],
                request.Target));

        var tech = result.Technology!;
        var stack = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor)
                        .Select(c => tech.Layers.Single(l => c.DrawingLayers.Contains(l.Key)).Name);
        Assert.Equal(["foil-1", "foil-2", "foil-3", "foil-4"], stack);
    }

    /// <summary>Artwork with no <c>%TF.FileFunction</c>: the name is the only evidence.</summary>
    private static string Artwork(double xMm, int flashes = 1) =>
        "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.400*%\nD10*\n" +
        string.Concat(Enumerable.Range(0, flashes).Select(k =>
            $"X{(long)Math.Round(xMm * 1_000_000)}Y{1_000_000 + k * 1_000_000}D03*\n")) +
        "M02*\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IReadOnlyList<string> FilesIn(string dir) =>
        [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];
}
