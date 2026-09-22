// ================================================================
//  SeriesTraceabilityTests.cs — the gate for brief-lvs-0-overview.md §3A.
//
//  ── WHY THE PLAN IS CHECKED AGAINST THE NOTE BY A TEST ────────────────────────────────────────
//
//  The design note numbers its requirements `R-lvs-1 … R-lvs-55` and the overview's §2A maps each
//  one to the brief that builds it. A requirement added to the note and not to that table is a
//  feature nobody is building, and nothing about reading either document makes it visible — the
//  table still LOOKS complete. Parsing both and comparing the sets is the only way the two stay in
//  step, which is what R-lvs0-1 asks for.
//
//  R-lvs0-2 is the other half and it is about the map being navigable: a brief the table names and
//  a link the overview carries must both resolve to a file that exists.
// ================================================================

using System.Linq;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class SeriesTraceabilityTests
{
    // ══ R-lvs0-1 — every note requirement has a brief, and every row names a real one ═══════════

    [Fact]
    public void TheTraceabilityTableCoversEveryRequirementInTheNote()
    {
        var note = new SortedSet<int>(
            Requirements(Read("docs", "design", "lvs.md")));
        var table = new SortedSet<int>(
            Requirements(Overview(), suffix: @"\s*\|"));

        Assert.NotEmpty(note);
        Assert.Equal("", Missing(note, table));
        Assert.Equal("", Missing(table, note));

        // Not vacuous: the note numbers them from 1 with no hole, which is the shape the table is
        // asserted against.
        Assert.Equal(Enumerable.Range(1, note.Max()), note);

        // Every brief the table names is one of the fifteen.
        foreach (int brief in Regex.Matches(Overview(), @"R-lvs-\d+\s*\|([^|]*)\|")
                     .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"\d+"))
                     .Select(m => int.Parse(m.Value)))
            Assert.InRange(brief, 0, 15);
    }

    // ══ R-lvs0-2 — every brief exists, and every link resolves ══════════════════════════════════

    [Fact]
    public void EveryBriefInTheSeriesExistsAndEveryLinkResolves()
    {
        string root = RepoRoot();
        string briefs = Path.Combine(root, "docs", "sonnet-briefs");

        for (int n = 1; n <= 15; n++)
            Assert.True(
                Directory.EnumerateFiles(briefs, $"brief-lvs-{n}-*.md").Any(),
                $"brief {n} of the LVS series has no file");

        foreach (System.Text.RegularExpressions.Match link in Regex.Matches(Overview(), @"\]\(([^)#]+)(?:#[^)]*)?\)"))
        {
            string target = link.Groups[1].Value;
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            string resolved = Path.GetFullPath(Path.Combine(briefs, target));
            Assert.True(File.Exists(resolved) || Directory.Exists(resolved),
                        $"brief-lvs-0-overview.md links to '{target}', which does not exist");
        }
    }

    // ── reading the two documents ────────────────────────────────────────────

    private static string Overview()
        => Read("docs", "sonnet-briefs", "brief-lvs-0-overview.md");

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static IEnumerable<int> Requirements(string text, string suffix = @"\b")
        => Regex.Matches(text, @"R-lvs-(\d+)" + suffix).Select(m => int.Parse(m.Groups[1].Value));

    private static string Missing(IEnumerable<int> from, IEnumerable<int> against)
        => string.Join(", ", from.Except(against).Select(n => $"R-lvs-{n}"));

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
