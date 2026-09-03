using System.Linq;
using System.Text;
using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>Gate 9 (brief-L4c-gerber-export.md §5): M48/METRIC header, tool table deduped by
/// diameter, hits at the right coordinates, M30 well-formed.</summary>
public class ExcellonWriterTests
{
    private static readonly GerberFormat Format = GerberUnits.Resolve(1000);

    private static string WriteToText(IReadOnlyList<ViaShape> vias)
    {
        using var ms = new MemoryStream();
        ExcellonWriter.Write(ms, vias, Format);
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    [Fact]
    public void Header_M48_Metric_And_M30_Trailer_Present()
    {
        var text = WriteToText([new ViaShape { X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 }]);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("M48", lines[0]);
        Assert.Contains("METRIC", lines);
        Assert.Equal("M30", lines[^1]);
    }

    [Fact]
    public void ToolsDedupedByDiameter_SameDrillSizeSharesOneTool()
    {
        var vias = new List<ViaShape>
        {
            new() { X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 },
            new() { X = 1000, Y = 1000, PadSize = 500_000, DrillSize = 300_000 },
            new() { X = 2000, Y = 2000, PadSize = 500_000, DrillSize = 500_000 },
        };

        var result = ExcellonWriter.Write(Stream.Null, vias, Format);
        Assert.Equal(2, result.ToolsDefined);
        Assert.Equal(3, result.HitsWritten);

        var text = WriteToText(vias);
        Assert.Equal(1, Count(text, "T1C"));
        Assert.Equal(1, Count(text, "T2C"));
    }

    [Fact]
    public void ToolDiameter_FormattedAsExactDecimalMillimetres()
    {
        var text = WriteToText([new ViaShape { X = 0, Y = 0, PadSize = 500_000, DrillSize = 300_000 }]);
        Assert.Contains("T1C0.300000", text);
    }

    [Fact]
    public void HitCoordinates_MatchViaPositionExactly()
    {
        var text = WriteToText([new ViaShape { X = 1_234_500, Y = -987_650, PadSize = 500_000, DrillSize = 300_000 }]);
        Assert.Contains("X1.234500Y-0.987650", text);
    }

    [Fact]
    public void ToolSelection_ChangesOnlyWhenDiameterChanges()
    {
        var vias = new List<ViaShape>
        {
            new() { X = 0, Y = 0, DrillSize = 300_000 },
            new() { X = 1000, Y = 0, DrillSize = 300_000 }, // same tool — no redundant T-select expected between hits
            new() { X = 2000, Y = 0, DrillSize = 500_000 }, // different tool
        };

        var lines = WriteToText(vias).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Exactly one bare "T1" selection line (before the first two hits) and one bare "T2" (before the third).
        Assert.Equal(1, lines.Count(l => l == "T1"));
        Assert.Equal(1, lines.Count(l => l == "T2"));
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
