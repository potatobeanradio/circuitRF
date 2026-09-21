using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// brief-footprint-4-picker-and-import.md §5 — one picker over built-ins, the workspace's own cells
/// and Custom, and the Component Import reconciliation.
///
/// <para>Test 1 is the reconciliation itself, as a test: <b>a part imported through
/// <c>ComponentImport.Import</c> is offered by the picker</b>, with no second step, no registration
/// and no second index — because a footprint IS a layout view of a cell and there is no second
/// artifact kind.</para>
/// </summary>
public sealed class FootprintCatalogTests : IDisposable
{
    private readonly string _root;

    public FootprintCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-footprint4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile());
        FootprintCatalog.Invalidate();
    }

    public void Dispose()
    {
        FootprintCatalog.Invalidate();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ══ 1-3. The imported part, its variants, and the pad-count filter ══════════════════════════

    /// <summary>
    /// Gates 1, 2 and 3 together, because they are three assertions about ONE import and splitting
    /// them would import the same synthetic part three times to ask three questions about it.
    /// </summary>
    [Fact]
    public void AnImportedPartIsOfferedAsOneRowPerVariantAndOnlyToAMatchingPortCount()
    {
        var result = ComponentImport.Import(
            TwoPadPart("Widget2", "", DensityVariant.Most, DensityVariant.Least),
            _root, destTech: null, destDbuPerMicron: LayoutUnits.DefaultDbuPerMicron);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.CellDir);

        string schematicDir = CellFolder.SubFolderPath(MakeCell("Board"), ViewType.Schematic);

        // ── 1. It is offered. THIS IS THE RECONCILIATION. ───────────────────────────────────────
        var two = FootprintCatalog.Build(_root, schematicDir, portCount: 2);
        var mine = two.Choices.Where(c => c.Section == FootprintSection.Workspace).ToList();
        Assert.All(mine, c => Assert.Equal("Widget2", c.CellName));

        // ── 2. Three variants are three rows, each labelled, and the primary is not privileged. ──
        Assert.Equal(3, mine.Count);
        Assert.Single(mine, c => c.IsPrimaryView);
        Assert.Equal(
            [DensityVariant.Nominal, DensityVariant.Least, DensityVariant.Most],
            mine.Select(c => c.Variant).OrderBy(v => v, StringComparer.Ordinal));
        Assert.Contains(mine, c => c.Display.Contains("density M", StringComparison.Ordinal));
        Assert.Contains(mine, c => c.Display.Contains("density L", StringComparison.Ordinal));
        Assert.All(mine, c => Assert.Contains("2 pads", c.Display, StringComparison.Ordinal));

        // Every row's reference resolves — and the two non-primary ones say, in advance, that an
        // instance draws the primary. Offered and explained, not hidden and not silently
        // substituted.
        foreach (var c in mine)
        {
            var r = FootprintCatalog.Resolve(c.Reference, schematicDir);
            Assert.Equal(FootprintCatalog.FootprintState.Cell, r.State);
            Assert.Equal(2, r.PadCount);
            if (c.IsPrimaryView) Assert.Null(c.NotPlaceable);
            else                 Assert.Contains("primary", c.NotPlaceable!, StringComparison.Ordinal);
        }

        // The other two sections are there, in their places (R-fp2-4a's None first, Custom last).
        Assert.Equal(FootprintSection.None,   two.Choices[0].Section);
        Assert.Equal(FootprintSection.Custom, two.Choices[^1].Section);
        Assert.Equal(SmtCaseTable.All.Count,  two.Choices.Count(c => c.Section == FootprintSection.BuiltIn));

        // ── 3. R-fp4-1b: not offered to an S4P, and neither is any built-in. ────────────────────
        var four = FootprintCatalog.Build(_root, schematicDir, portCount: 4);
        Assert.Empty(four.Choices.Where(c => c.Section is FootprintSection.Workspace or FootprintSection.BuiltIn));
    }

    // ══ 4. Component Import is untouched ════════════════════════════════════════════════════════

    /// <summary>
    /// R-fp4-2a, as a comment-stripped source scan: this series adds no code to
    /// <c>ComponentImport.cs</c> beyond a call site, writes no second land-pattern format and
    /// registers no second footprint index.
    /// </summary>
    [Fact]
    public void ComponentImportGainedOneCallSiteAndNoSecondLandPatternWriter()
    {
        string code = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Design", "Layout", "ComponentImport.cs")));

        // Exactly one mention of the catalog, and it is the report's call site — not a registration,
        // not a second index, not a footprint type of its own.
        Assert.Equal(1, Regex.Matches(code, @"FootprintCatalog").Count);
        Assert.Contains("FootprintCatalog.ImportAvailabilitySentence(", code, StringComparison.Ordinal);

        // One writer of a land pattern, the one that was already here. A second would be the second
        // artifact kind the series' governing rule forbids.
        Assert.Equal(1, Regex.Matches(code, @"LayoutPersistence\.SaveToFile").Count);
    }

    // ══ 5. One density spelling ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-fp4-2c. An imported nominal pattern's <c>BuiltLayout.Variant</c> and a generated nominal
    /// pattern's suffix must be the same string FROM THE SAME CONSTANT — otherwise the two sort into
    /// two rows reading the same word.
    /// </summary>
    [Fact]
    public void AnImportedNominalVariantAndAGeneratedOneAreOneSpellingFromOneConstant()
    {
        var built = ComponentImport.Build(
            TwoPadPart("Widget2", "", DensityVariant.Most), null,
            LayoutUnits.DefaultDbuPerMicron, []);
        Assert.NotNull(built);

        var nominal = built!.Layouts.Single(l => l.Variant.Length == 0);
        Assert.Equal(DensityVariant.Nominal, nominal.Variant);
        Assert.Equal(DensityVariant.Nominal,
                     FootprintRef.For(SmtCaseTable.All[0], DensityLevel.Nominal).VariantSuffix);
        Assert.Equal(DensityVariant.Most,
                     FootprintRef.For(SmtCaseTable.All[0], DensityLevel.Most).VariantSuffix);

        // And the reader's own list IS that constant, not a fourth copy of it: the underscore
        // spelling a decal name carries reads back as the same level.
        Assert.Equal(DensityLevel.Most,  DensityVariant.LevelOf("_M"));
        Assert.Equal(DensityVariant.Most, DensityVariant.Canonical("_M"));
    }

    // ══ 6-7. The BOM column ═════════════════════════════════════════════════════════════════════

    /// <summary>R-fp4-3a/b — the normalisation, as a table of examples.</summary>
    [Theory]
    [InlineData("SM/C_0402",    FootprintTokenOutcome.Matched,   "0402")]
    [InlineData("C0402",        FootprintTokenOutcome.Matched,   "0402")]
    [InlineData("CAP-0402-X7R", FootprintTokenOutcome.Matched,   "0402")]
    [InlineData("0402M",        FootprintTokenOutcome.Matched,   "0402")]
    [InlineData("0402",         FootprintTokenOutcome.Ambiguous, null)]
    [InlineData("WIDGET-77",    FootprintTokenOutcome.Unmatched, null)]
    public void ABomTokenIsReducedExplicitlyOrReportedAsWritten(
        string token, FootprintTokenOutcome expected, string? code)
    {
        var m = FootprintTokens.Match(token);
        Assert.Equal(expected, m.Outcome);
        Assert.Equal(code, m.Case?.Code);

        // R-fp4-3b: a token that matches nothing is shown AS WRITTEN and never turned into the
        // nearest code — the whole point of the column is that it tells you something.
        if (expected != FootprintTokenOutcome.Matched) Assert.Null(m.Reference);
        Assert.Contains(token, m.Report, StringComparison.Ordinal);

        // The density letter is brief 1's own @M/@N/@L, not a scheme marker.
        if (token == "0402M") Assert.Equal("smt:0402@M", m.Reference);
    }

    /// <summary>
    /// R-fp4-3c — the ambiguity refusal fires on the colliding set and NOT outside it, and it names
    /// both readings. <c>0201</c> imperial is 0.60 x 0.30 mm and <c>0201</c> metric is 0.25 x 0.125 mm,
    /// a factor of 2.4 with nothing to notice.
    /// </summary>
    [Fact]
    public void TheAmbiguityRefusalFiresOnTheCollidingSetAndNotOutsideIt()
    {
        Assert.Equal(
            ["0201", "0402", "0603", "1005", "1608", "2012", "3216", "3225"],
            FootprintTokens.AmbiguousTokens);

        foreach (string token in FootprintTokens.AmbiguousTokens)
        {
            var m = FootprintTokens.Match(token);
            Assert.Equal(FootprintTokenOutcome.Ambiguous, m.Outcome);
            // Both readings named, and the two spellings that answer it — the same shape convert's
            // Excellon suppression refusal takes.
            Assert.Contains("imperial", m.Report, StringComparison.Ordinal);
            Assert.Contains("metric",   m.Report, StringComparison.Ordinal);
            Assert.Contains("smt:",     m.Report, StringComparison.Ordinal);
        }

        // Outside the set: a bare token with no metric twin to be confused with resolves cleanly,
        // and so does a tantalum code, whose separator the reduction must not split on.
        Assert.Equal("2512",    FootprintTokens.Match("2512").Case?.Code);
        Assert.Equal("7343-31", FootprintTokens.Match("7343-31").Case?.Code);
    }

    // ══ 8. The walk is bounded ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-fp4-1c. A deep tree and a directory symlink: the catalog returns, says it stopped short,
    /// and did not follow the link.
    /// </summary>
    [Fact]
    public void TheWalkIsBoundedSaysSoAndNeverFollowsADirectorySymlink()
    {
        // A cell two levels down, which a depth-1 walk cannot reach.
        string deep = Path.Combine(_root, "lib", "vendor");
        Directory.CreateDirectory(deep);
        ComponentImport.Import(TwoPadPart("Deep", ""), deep, null, LayoutUnits.DefaultDbuPerMicron);

        FootprintCatalog.Invalidate();
        var shallow = FootprintCatalog.WorkspaceViews(_root, maxDepth: 1);
        Assert.True(shallow.StoppedShort);
        Assert.Empty(shallow.Views);

        FootprintCatalog.Invalidate();
        var full = FootprintCatalog.WorkspaceViews(_root, maxDepth: FootprintCatalog.DefaultDepth);
        Assert.False(full.StoppedShort);
        Assert.Single(full.Views);

        // A link BACK to the root: followed, this walk would not terminate. It is skipped, and it is
        // not an error — the link is simply not a place the catalog reports on.
        string link = Path.Combine(_root, "loop");
        Directory.CreateSymbolicLink(link, _root);

        FootprintCatalog.Invalidate();
        var withLink = FootprintCatalog.WorkspaceViews(_root, maxDepth: FootprintCatalog.DefaultDepth);
        Assert.Single(withLink.Views);
        Assert.DoesNotContain(withLink.Views, v => v.CellDir.Contains("loop", StringComparison.Ordinal));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A synthetic two-pad part with one land pattern per <paramref name="variants"/> entry.
    /// Synthetic on purpose — §4 of the series overview: every part in every test stays so.</summary>
    private static ComponentPart TwoPadPart(string name, params string[] variants)
    {
        var part = new ComponentPart { Name = name };
        foreach (string variant in variants)
        {
            var fp = new ComponentFootprint { Name = name + variant, Variant = variant };
            foreach (var (pad, x) in new[] { ("1", -500_000L), ("2", 500_000L) })
            {
                fp.Cell.Pins.Add(new PcbImportedPin(
                    new LayoutPin { Name = pad, X = x, Y = 0, WidthDbu = 400_000 }, "F.Cu"));
                fp.PadNames.Add(pad);
            }
            part.Footprints.Add(fp);
        }
        return part;
    }

    private string MakeCell(string cellName)
    {
        string dir = CellFolder.CreateCellFolder(_root, cellName);
        FootprintCatalog.Invalidate();
        return dir;
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
