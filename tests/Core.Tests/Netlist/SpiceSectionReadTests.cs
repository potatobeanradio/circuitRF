using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Reading ONE <c>.lib</c> section of a file — the format feature the extension is named after.
///
/// <para>The reader has tracked <c>.LIB</c>/<c>.ENDL</c> framing since it was written, and
/// <c>PdkCorners</c> has used it; what it did not have was a way for anyone ELSE to ask, because
/// <see cref="SpiceNetlistReader.ReadFile"/> and <see cref="SpiceNetlistReader.Read"/> both hard-coded
/// <c>section: null</c>. These pin the two public entry points and the one property that makes a
/// section CHOOSABLE rather than merely readable: a whole-file read has to report the alternatives it
/// skipped, or nothing upstream can offer them.</para>
/// </summary>
public sealed class SpiceSectionReadTests
{
    /// <summary>Two sections, each defining a different part, and nothing outside either.</summary>
    private const string TwoSections = """
        * a library offering two alternatives
        .lib nominal
        .subckt PART_A p n
        R1 p n 1k
        .ends
        .endl

        .lib fast
        .subckt PART_B p n
        R1 p n 2k
        .ends
        .endl
        """;

    private static string Write(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "crf-sec-" + System.Guid.NewGuid().ToString("N")[..8] + ".lib");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void WithNoSection_NothingIsRead_AndBothAlternativesAreNamed()
    {
        var path = Write(TwoSections);
        try
        {
            var result = SpiceNetlistReader.ReadFile(path);

            // Sections are ALTERNATIVES. Reading one nobody asked for is a guess, and reading both
            // would produce a library holding two mutually exclusive versions of the same thing.
            Assert.Empty(result.Library.Cells);

            var offered = Assert.Single(result.Sections);
            Assert.Equal(path, offered.File, ignoreCase: true);
            Assert.Equal(["nominal", "fast"], offered.Names);

            // And it SAYS so, per section, rather than reading as a file full of mysteries.
            Assert.Equal(2, result.Notes.Count(n => n.Message.Contains("none was requested")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AskingForOneSection_ReadsThatSectionOnly()
    {
        var path = Write(TwoSections);
        try
        {
            var nominal = SpiceNetlistReader.ReadFile(path, "nominal");
            Assert.Equal(["PART_A"], nominal.Library.Cells.Select(c => c.Name));

            var fast = SpiceNetlistReader.ReadFile(path, "fast");
            Assert.Equal(["PART_B"], fast.Library.Cells.Select(c => c.Name));

            // Both readings still learn what the file OFFERS — the framing lines are seen either way,
            // which is what lets one read serve "which are there" and "what is in this one".
            Assert.Equal(["nominal", "fast"], Assert.Single(fast.Sections).Names);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AskingForASectionTheFileDoesNotDeclare_ReportsWhatItDoesOffer()
    {
        var path = Write(TwoSections);
        try
        {
            var result = SpiceNetlistReader.ReadFile(path, "slow");

            Assert.Empty(result.Library.Cells);

            var note = Assert.Single(result.Notes, n => n.Message.Contains("does not declare it"));
            Assert.Contains("nominal", note.Message);
            Assert.Contains("fast", note.Message);

            // The note is ALL the reader can say. `MarkIncomplete` records the enclosing subcircuit,
            // and there is no enclosing subcircuit at end-of-file — so this case reads as "an empty
            // file", and the gestures above it (the picker's Section combo, the SpiceModel panel's
            // status line) are what turn it back into a sentence naming the alternatives.
            Assert.Empty(result.IncompleteCells);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ABlankSectionNameIsNoSectionAtAll_NotASectionCalledEmpty()
    {
        // The value arrives from a stored component parameter and from a combo box, both of which
        // spell "unset" as "". A "" that reached the framing test would report every section as one
        // the file does not declare and then read nothing from any of them.
        var path = Write(TwoSections);
        try
        {
            var blank = SpiceNetlistReader.ReadFile(path, "   ");

            Assert.Empty(blank.Library.Cells);
            Assert.DoesNotContain(blank.Notes, n => n.Message.Contains("does not declare it"));
            Assert.Empty(blank.IncompleteCells);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AFileWithNoSectionsIsUnaffected_WhichIsTheOverwhelmingMajority()
    {
        var path = Write("""
            .subckt PLAIN p n
            R1 p n 1k
            .ends
            """);
        try
        {
            var result = SpiceNetlistReader.ReadFile(path);
            Assert.Equal(["PLAIN"], result.Library.Cells.Select(c => c.Name));
            Assert.Empty(result.Sections);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadFromText_TakesASectionToo()
    {
        var result = SpiceNetlistReader.Read(TwoSections, sourceDirectory: null, fileLabel: "lib.lib",
                                             section: "fast");
        Assert.Equal(["PART_B"], result.Library.Cells.Select(c => c.Name));
    }
}
