using System;
using System.IO;
using System.Linq;
using RfCore;
using Xunit;

namespace RfCore.Tests;

/// <summary>Which numbered port is which named port: written as <c>Port[N] = name</c> header comments, read back from the
/// header alone, and re-read when the file changes.</summary>
public sealed class TouchstonePortNamesTests
{
    [Fact]
    public void WrittenLines_ReadBackFromAFilesHeader_AndAChangedFileIsReadAgain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-ports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "board.s3p");
        try
        {
            var lines = TouchstonePortNames.Lines([(2, "C1"), (1, " P1 "), (3, "")]);
            Assert.Equal(["Port[1] = P1", "Port[2] = C1"], lines);     // number order, trimmed; a blank name writes nothing

            // Data lines after the option line are never read: a "Port[3]" there would otherwise name port 3.
            File.WriteAllLines(path, [.. lines.Select(l => "! " + l), "! other comment", "# GHz S RI R 50",
                                      "1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0", "! Port[3] = late"]);
            Assert.Equal(["P1", "C1", null], TouchstonePortNames.FromFile(path, 3)!);

            File.WriteAllLines(path, ["! Port[3] = U1.OUT", "# GHz S RI R 50"]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            Assert.Equal([null, null, "U1.OUT"], TouchstonePortNames.FromFile(path, 3)!);

            File.WriteAllLines(path, ["! no names", "# GHz S RI R 50"]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
            Assert.Null(TouchstonePortNames.FromFile(path, 3));
            Assert.Null(TouchstonePortNames.Parse(["Port[4] = beyond"], 3));       // a number outside the file's ports
        }
        finally { Directory.Delete(dir, true); }
    }
}
