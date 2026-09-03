using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>R-L4c-2: the .gbrjob file is "the X2 answer to which files belong together" — a minimal
/// but valid job-file JSON listing the file set.</summary>
public class GerberJobFileTests
{
    [Fact]
    public void Write_ProducesValidJson_ListingEveryFile()
    {
        var files = new List<GerberJobFile.FileAttribute>
        {
            new("board.GTL", "Copper,L1,Top"),
            new("board.GBL", "Copper,L2,Bot"),
            new("board.drl", null),
        };

        using var ms = new MemoryStream();
        GerberJobFile.Write(ms, files, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "1.2.3");
        ms.Position = 0;

        using var doc = JsonDocument.Parse(ms);
        var root = doc.RootElement;

        Assert.Equal("1.2.3", root.GetProperty("Header").GetProperty("GenerationSoftware").GetProperty("Version").GetString());
        Assert.Equal("2026-01-01T00:00:00Z", root.GetProperty("Header").GetProperty("CreationDate").GetString());

        var entries = root.GetProperty("FilesAttributes").EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.GetProperty("Path").GetString() == "board.GTL" &&
                                       e.GetProperty("FileFunction").GetString() == "Copper,L1,Top" &&
                                       e.GetProperty("FilePolarity").GetString() == "Positive");
        Assert.Contains(entries, e => e.GetProperty("Path").GetString() == "board.drl" &&
                                       !e.TryGetProperty("FileFunction", out _));
    }
}
