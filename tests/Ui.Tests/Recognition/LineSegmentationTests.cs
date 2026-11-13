// brief-artsch-5-traces-to-line-elements.md §4 — straight runs, corners, chamfers, tapers and slivers
// (R-as5-3 … R-as5-5), on synthetic boards built in memory.

using System.Linq;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.LineBoards;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class LineSegmentationTests
{
    [Fact]
    public void AStraightLineBetweenTwoPadsIsOneMlinFromPadEdgeToPadEdge()
    {
        var (view, padToPad) = TwoPartsInSeries();
        var result = Recognize(view, FourLayer());

        var between = Lines(result).Where(e =>
        {
            var ends = e.Nodes.Select(n => NodeOf(result, n)).ToList();
            return ends.All(n => n?.Kind == LineNodeKind.PartTerminal) && ends[0]!.Refdes != ends[1]!.Refdes;
        }).ToList();
        var line = Assert.Single(between);
        Assert.Equal(LineElementType.MLIN, line.Type);
        Assert.Equal(380, line.Micro("W"), 0.5);
        Assert.Equal(padToPad, line.Micro("L"), 1.0);
    }

    [Fact]
    public void ARightAngleCornerIsMlinMbendMlinWhoseLengthsAndCornerSquareMakeTheCentreLine()
    {
        var result = Recognize(Corner(), TwoLayer());

        var types = result.Lines.Elements.Select(e => e.Type).ToList();
        Assert.Equal([LineElementType.MLIN, LineElementType.MBEND, LineElementType.MLIN], types);
        var bend = result.Lines.Elements[1];
        Assert.Equal(90, bend.Parameters["Angle"], 0.5);
        Assert.Equal((double)MicrostripBendMiter.None, bend.Parameters["Miter"]);
        double sum = result.Lines.Elements[0].Micro("L") + result.Lines.Elements[2].Micro("L") + W;
        Assert.Equal(15_000 + 20_000, sum, 1.0);
    }

    [Fact]
    public void AChamferedCornerReadsItsMiter()
    {
        double leg = MicrostripDiscontinuities.MiterCutLength(W * 1e-6, 500e-6) * 1e6;
        var result = Recognize(Corner(leg), TwoLayer());

        var bend = Assert.Single(result.Lines.OfType(LineElementType.MBEND));
        Assert.Equal((double)MicrostripBendMiter.Optimal, bend.Parameters["Miter"]);
    }

    [Fact]
    public void ALinearRampIsAnMtaper()
    {
        var result = Recognize(Taper(), TwoLayer());

        var taper = Assert.Single(result.Lines.OfType(LineElementType.MTAPER));
        Assert.Equal([500.0, 1500.0], new[] { taper.Micro("W1"), taper.Micro("W2") }.Order().Select(w => System.Math.Round(w)));
        Assert.Equal(1_000, taper.Micro("L"), 1.0);
    }

    [Fact]
    public void ASliverIsAbsorbedNeverDropped()
    {
        var result = Recognize(Sliver(), TwoLayer());

        var line = Assert.Single(Lines(result));
        Assert.Equal(60, line.Micro("W"), 0.5);
        Assert.Equal(6_050, line.Micro("L"), 1.0);
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.SliversAbsorbed));
    }

    /// <summary>
    /// Two terminals at one point either side of a 3:1 diagonal line, 8 nm off its centre line, project 1.9e-9 nm apart: the
    /// second ahead of the first when the line is walked one way, behind it the other way. Walked either way, both close a line
    /// at the one point — the second is the first's node — and neither floats; with no tolerance the one ahead took the end of
    /// the atom after the split, the line's last, where no line closes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoTapsAtOnePointOfADiagonalLine_AreOneNode_WhicheverWayItIsWalked(bool reversed)
    {
        var (a, b) = ((0L, 0L), (30_000_000L, 10_000_000L));
        if (reversed) (a, b) = (b, a);
        var run = new TraceRun { Id = "T1", Layer = Top, Pieces = [(a.Item1, a.Item2, b.Item1, b.Item2, 1_000_000.0)] };
        var counts = new SegmentationCounts();
        var lines = LineSegmentation.Segment(run, 0, new RecognitionOptions(), new LineBinder(TwoLayer(), "Top"),
                                             CircuitRF.Design.Layout.LayoutUnits.DefaultDbuPerMicron, counts,
                                             [("C6.1", 15_000_000 - 8, 5_000_000 + 24), ("C7.1", 15_000_000 + 8, 5_000_000 - 24)]);

        Assert.Equal(["C6.1", "C7.1"], lines.Taps.Order());
        var drafts = lines.Drafts.Where(d => d.IsLine).ToList();
        Assert.Equal(2, drafts.Count);
        string at = drafts[0].Nodes[^1];
        Assert.Equal(at, drafts[1].Nodes[0]);
        string other = at == "C6.1" ? "C7.1" : "C6.1";
        Assert.Contains((other, at), lines.TapAliases);
    }
}
