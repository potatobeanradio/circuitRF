// Recognition's check on itself: the circuit must join what the copper joins, and nothing more (CircuitCopperCheck).

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class CircuitCopperCheckTests
{
    private static LineNode At(string name, LineNodeKind kind, int island, double xMm) => new(name, kind, island, (long)(xMm * 1e6), 0, null);

    private static LineElement Line(string name, string a, string b) =>
        new() { Name = name, Type = LineElementType.MLIN, Nodes = [a, b], Parameters = new Dictionary<string, double>() };

    private static RecognitionReport Check(IReadOnlyList<LineElement> elements, IReadOnlyList<LineNode> attachments,
                                           Dictionary<string, string>? joined = null)
    {
        var report = new RecognitionReport();
        var lines = new LineRecognitionResult(elements, []) { Attachments = attachments, Joined = joined ?? [] };
        CircuitCopperCheck.Check([], lines, report, RailLengthFormat.For(new LayoutView()));
        return report;
    }

    /// <summary>A line from P1 to C1's terminal and a line from C1's other terminal to C2's, each pair on one island, with
    /// C2's terminal joined to its node by name: one summary line, and nothing else.</summary>
    [Fact]
    public void ACircuitThatJoinsWhatTheCopperJoins_SaysSoOnce()
    {
        var report = Check([Line("TL1", "P1", "C1.1"), Line("TL2", "C1.2", "N1")],
            [At("P1", LineNodeKind.Port, 0, 0), At("C1.1", LineNodeKind.PartTerminal, 0, 5), At("C1.2", LineNodeKind.PartTerminal, 1, 6),
             At("C2.1", LineNodeKind.PartTerminal, 1, 9)],
            joined: new() { ["C2.1"] = "N1" });
        Assert.Equal(4, report.Count(RecognitionFindingClass.CircuitAgreesWithCopper));
        Assert.Equal(0, report.Count(RecognitionFindingClass.CircuitSplitsCopper));
        Assert.Equal(0, report.Count(RecognitionFindingClass.CircuitJoinsAcrossCopper));
    }

    /// <summary>A pad whose members became separate nodes, and a line that runs from one island to another: both are
    /// findings naming the two places, and the summary is not said.</summary>
    [Fact]
    public void ASplitIsland_AndAJoinAcrossTwoIslands_AreEachReportedWithBothPlaces()
    {
        var report = Check([Line("TL1", "P1", "O1"), Line("TL2", "C2.1", "P2")],
            [At("P1", LineNodeKind.Port, 0, 0), At("C1.1", LineNodeKind.PartTerminal, 0, 5), At("C2.1", LineNodeKind.PartTerminal, 1, 9),
             At("P2", LineNodeKind.Port, 2, 20)]);
        var split = report.Of(RecognitionFindingClass.CircuitSplitsCopper)!;
        Assert.Equal(1, split.Count);
        Assert.Matches(@"copper joins P1 at \(0, 0\) \S+ and C1\.1 at \(5000, 0\) \S+, the circuit does not", split.Sentence);
        Assert.Equal(2, split.Anchors.Count);
        var join = report.Of(RecognitionFindingClass.CircuitJoinsAcrossCopper)!;
        Assert.Matches(@"the circuit joins C2\.1 at \(9000, 0\) \S+ and P2 at \(20000, 0\) \S+, the copper does not", join.Sentence);
        Assert.Null(report.Of(RecognitionFindingClass.CircuitAgreesWithCopper));
    }
}
