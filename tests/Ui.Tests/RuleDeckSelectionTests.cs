using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout.TechImport;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Which of a process's deck files are ONE deck. Every fixture is synthetic — the repository commits
/// no third-party process data — and reproduces the deck language's own include grammar, which is
/// what the selection actually keys on.
/// </summary>
public class RuleDeckSelectionTests
{
    private static (string, string) F(string path, string text) => (path, text);

    private static string P(params string[] parts)
        => Path.GetFullPath(Path.Combine([Path.GetTempPath(), "deck", .. parts]));

    /// <summary>
    /// The headline. A root that pulls its rule files in defines a deck; a self-contained file that
    /// pulls in nothing and is pulled in by nobody is a DIFFERENT deck and must not be merged.
    /// </summary>
    [Fact]
    public void TheIncludeRootedDeckIsSelected_AndAStandaloneDeckIsReportedNotMerged()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("main.drc"), """
                # %include rule_decks/layers.drc
                # %include rule_decks/metal1.drc
                m1_core = metal1_drw.not(filler_drw)
                """),
            F(P("rule_decks", "layers.drc"),  "metal1_drw = get_polygons(8, 0)\n"),
            F(P("rule_decks", "metal1.drc"),  "l = m1_core.width(0.16.um)\nl.output('M1.a', \"w\")\n"),
            F(P("rule_decks", "alt.drc"),     "x = y.not(z)\n"),
        ]);

        Assert.Equal(P("main.drc"), sel.RootPath);
        Assert.Equal(3, sel.MainSet.Count);
        Assert.Equal([P("rule_decks", "alt.drc")], sel.Alternates);
    }

    /// <summary>The root comes first so a deck is presented in its own order.</summary>
    [Fact]
    public void TheRootIsFirstInTheMainSet()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("a.drc"), "# %include b.drc\n"),
            F(P("b.drc"), "x = 1\n"),
        ]);

        Assert.Equal(P("a.drc"), sel.MainSet[0]);
    }

    /// <summary>Includes are transitive — a deck routinely nests one level of grouping file.</summary>
    [Fact]
    public void IncludesAreFollowedTransitively()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("root.drc"),        "# %include sub/group.drc\n"),
            F(P("sub", "group.drc"), "# %include leaf.drc\n"),
            F(P("sub", "leaf.drc"),  "x = 1\n"),
        ]);

        Assert.Equal(3, sel.MainSet.Count);
        Assert.Empty(sel.Alternates);
    }

    /// <summary>An include names a path relative to the INCLUDING file, not to the scan root.</summary>
    [Fact]
    public void AnIncludeResolvesRelativeToItsOwnFile()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("d", "root.drc"),      "# %include inner/leaf.drc\n"),
            F(P("d", "inner", "leaf.drc"), "x = 1\n"),
        ]);

        Assert.Contains(P("d", "inner", "leaf.drc"), sel.MainSet);
        Assert.Empty(sel.Alternates);
    }

    /// <summary>The plain script-level load form, for a deck with no preprocessor.</summary>
    [Fact]
    public void ThePlainLoadFormIsAlsoAnInclude()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("root.drc"), "load 'parts/one.drc'\n"),
            F(P("parts", "one.drc"), "x = 1\n"),
        ]);

        Assert.Equal(P("root.drc"), sel.RootPath);
        Assert.Empty(sel.Alternates);
    }

    /// <summary>
    /// A flat deck is one deck. Refusing to read it because nothing declared an include graph would
    /// be worse than the problem this solves, so no graph means the pre-existing behaviour exactly.
    /// </summary>
    [Fact]
    public void NoIncludeGraphAtAll_ReadsEverything()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("a.drc"), "x = 1\n"),
            F(P("b.drc"), "y = 2\n"),
        ]);

        Assert.Null(sel.RootPath);
        Assert.Equal(2, sel.MainSet.Count);
        Assert.Empty(sel.Alternates);
    }

    /// <summary>A deck that includes a file the scan never classified as a deck is still readable.</summary>
    [Fact]
    public void AnIncludeNamingAFileTheScanNeverFound_IsIgnoredRatherThanBreakingTheGraph()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("root.drc"), "# %include helpers/shared.rb\n# %include one.drc\n"),
            F(P("one.drc"),  "x = 1\n"),
        ]);

        Assert.Equal(P("root.drc"), sel.RootPath);
        Assert.Equal(2, sel.MainSet.Count);
    }

    /// <summary>Two independent decks: the larger is read, the other reported.</summary>
    [Fact]
    public void TwoRootedDecks_TheLargerIsRead_TheOtherReported()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("big.drc"),   "# %include b1.drc\n# %include b2.drc\n"),
            F(P("b1.drc"),    "x = 1\n"),
            F(P("b2.drc"),    "y = 2\n"),
            F(P("small.drc"), "# %include s1.drc\n"),
            F(P("s1.drc"),    "z = 3\n"),
        ]);

        Assert.Equal(P("big.drc"), sel.RootPath);
        Assert.Equal(3, sel.MainSet.Count);
        Assert.Equal([P("s1.drc"), P("small.drc")], sel.Alternates.OrderBy(x => x).ToList());
    }

    /// <summary>A deck that includes itself, directly or in a ring, must not hang the selection.</summary>
    [Fact]
    public void ACyclicIncludeGraph_Terminates()
    {
        var sel = RuleDeckSelector.Select(
        [
            F(P("a.drc"), "# %include b.drc\n"),
            F(P("b.drc"), "# %include a.drc\n# %include c.drc\n"),
            F(P("c.drc"), "x = 1\n"),
        ]);

        // Every file is mutually reachable, so nothing is left over — the point is that it returns.
        Assert.Equal(3, sel.MainSet.Count);
        Assert.Empty(sel.Alternates);
    }

    [Fact]
    public void NoFiles_IsNotAnError() =>
        Assert.Null(RuleDeckSelector.Select([]).RootPath);
}
