using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// A network extracted from a physical structure often exposes more ports than the part has pins —
/// the extra ones are openings left where lumped components attach. Getting the split wrong builds
/// a different circuit and still simulates, so these tests care as much about the refusals as the
/// answers.
/// </summary>
public class TouchstonePortLabelsTests
{
    private static string[] Lines(params string[] l) => l;

    // ── parsing ───────────────────────────────────────────────────────────────

    [Fact]
    public void PortLabels_AreReadInIndexOrder_RegardlessOfHowTheyAreWritten()
    {
        var labels = TouchstonePortLabels.Parse(Lines(
            "! Touchstone file from project SAMPLE",
            "! Port[3] = PAD_C_T1",
            "Port[1]=PAD_A_T1",
            "!    Port[2]   =   PAD_B_T1   "));

        Assert.Equal([1, 2, 3], labels.Select(l => l.Port));
        Assert.Equal(["PAD_A_T1", "PAD_B_T1", "PAD_C_T1"], labels.Select(l => l.Name));
    }

    [Fact]
    public void ANonPortComment_IsNotAPortLabel()
    {
        Assert.Empty(TouchstonePortLabels.Parse(Lines(
            "! Exported 2026-01-01", "! Variables:", "!  $X = 1170um", "# GHZ S MA R 50.0")));
    }

    [Fact]
    public void AMalformedPortLine_IsSkippedRatherThanGuessedAt()
    {
        Assert.Empty(TouchstonePortLabels.Parse(Lines(
            "! Port[] = A", "! Port[x] = B", "! Port[1] C", "! Port[2] =", "! Port[0] = D")));
    }

    [Fact]
    public void ARepeatedPort_KeepsTheFirstDeclaration()
    {
        var labels = TouchstonePortLabels.Parse(Lines("! Port[1] = FIRST", "! Port[1] = SECOND"));
        Assert.Equal("FIRST", Assert.Single(labels).Name);
    }

    // ── grouping ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("BLOCK_T1",   "BLOCK")]     // terminal index
    [InlineData("BLOCK_T12",  "BLOCK")]
    [InlineData("ARRAY_9_T3", "ARRAY_9")]   // only the LAST segment is the terminal index
    [InlineData("PIN:1",      "PIN")]       // modal index
    [InlineData("GRP_2:1",    "GRP_2")]
    [InlineData("PLAIN",      "PLAIN")]     // no suffix: the port is its own group
    [InlineData("NODE_T",     "NODE_T")]    // 'T' with no index is part of the name
    [InlineData("NODE_TX1",   "NODE_TX1")]
    public void AGroupIsTheLabelWithoutItsTerminalSuffix(string name, string expected) =>
        Assert.Equal(expected, TouchstonePortLabels.GroupOf(name));

    // ── the split ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExternalPortsRunUntilTheFirstMultiTerminalObject()
    {
        // Three singleton pads, then one object carrying eight terminals.
        var lines = new List<string> { "! Port[1] = PAD_A_T1", "! Port[2] = PAD_B_T1", "! Port[3] = PAD_C_T1" };
        for (int t = 1; t <= 8; t++) lines.Add($"! Port[{t + 3}] = ARRAY_1_T{t}");

        var split = TouchstonePortLabels.SplitExternal(TouchstonePortLabels.Parse(lines));

        Assert.Equal(PortSplitConfidence.Structural, split.Confidence);
        Assert.Equal(3, split.ExternalPortCount);
        Assert.Contains("ARRAY_1", split.Reason);
    }

    [Fact]
    public void EveryPortNamingADifferentObject_IsAmbiguous_NotAGuess()
    {
        // Two pads and twelve single-terminal attachment points: nothing marks where pins stop.
        var lines = new List<string> { "! Port[1] = AP1_T1", "! Port[2] = AP2_T1" };
        for (int k = 1; k <= 12; k++) lines.Add($"! Port[{k + 2}] = DPA{k}_T1");

        var split = TouchstonePortLabels.SplitExternal(TouchstonePortLabels.Parse(lines));

        Assert.Equal(PortSplitConfidence.Ambiguous, split.Confidence);
        Assert.Contains("different object", split.Reason);
    }

    [Fact]
    public void AMultiTerminalObjectAtPortOne_IsAmbiguous_NotZeroExternalPorts()
    {
        var split = TouchstonePortLabels.SplitExternal(TouchstonePortLabels.Parse(Lines(
            "! Port[1] = ARRAY_1_T1", "! Port[2] = ARRAY_1_T2", "! Port[3] = ARRAY_1_T3")));

        Assert.Equal(PortSplitConfidence.Ambiguous, split.Confidence);
        Assert.Contains("first port", split.Reason);
    }

    [Fact]
    public void AFileDeclaringNoLabels_IsAmbiguous()
    {
        var split = TouchstonePortLabels.SplitExternal(TouchstonePortLabels.Parse(Lines("! nothing here")));
        Assert.Equal(PortSplitConfidence.Ambiguous, split.Confidence);
        Assert.Contains("no port labels", split.Reason);
    }

    [Fact]
    public void TheSplitIsDecidedByPortORDER_NotByDeclarationOrder()
    {
        // The shared object is declared first but occupies the LAST ports.
        var split = TouchstonePortLabels.SplitExternal(TouchstonePortLabels.Parse(Lines(
            "! Port[3] = ARRAY_1_T1", "! Port[4] = ARRAY_1_T2",
            "! Port[1] = PAD_A_T1",   "! Port[2] = PAD_B_T1")));

        Assert.Equal(PortSplitConfidence.Structural, split.Confidence);
        Assert.Equal(2, split.ExternalPortCount);
    }
}
