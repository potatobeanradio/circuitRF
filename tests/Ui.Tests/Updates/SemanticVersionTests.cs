using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-6 — SemVer 2.0 precedence, pinned by a table. The ordering IS the requirement; an
/// implementation that parses correctly and orders wrong offers users a downgrade and calls it an
/// update.
/// </summary>
public class SemanticVersionTests
{
    /// <summary>
    /// The brief's ordering, verbatim. <c>beta.2 &lt; beta.10</c> is the case a naive implementation
    /// gets wrong, because dot-separated numeric identifiers compare NUMERICALLY, not as text.
    /// </summary>
    private static readonly string[] Ascending =
    [
        "0.9.0",
        "1.0.0-beta.1",
        "1.0.0-beta.2",
        "1.0.0-beta.10",
        "1.0.0-rc.1",
        "1.0.0",
    ];

    [Fact]
    public void OrderingTable_HoldsForEveryAdjacentPair()
    {
        for (int i = 0; i + 1 < Ascending.Length; i++)
        {
            SemanticVersion lo = SemanticVersion.Parse(Ascending[i]);
            SemanticVersion hi = SemanticVersion.Parse(Ascending[i + 1]);

            Assert.True(lo < hi, $"expected {lo} < {hi}");
            Assert.True(hi > lo, $"expected {hi} > {lo}");
            Assert.False(lo == hi);
        }
    }

    [Fact]
    public void OrderingTable_SortsBackIntoTheSameOrder()
    {
        // Shuffled deterministically — a seeded Random, so a failure reproduces.
        var rng = new Random(1729);
        List<SemanticVersion> shuffled = Ascending
            .Select(SemanticVersion.Parse)
            .OrderBy(_ => rng.Next())
            .ToList();

        Assert.Equal(Ascending, shuffled.OrderBy(v => v).Select(v => v.ToString()).ToArray());
    }

    [Fact]
    public void NumericPrereleaseIdentifiers_CompareNumerically_NotLexically()
    {
        // The one that matters: as text, "10" sorts before "2".
        Assert.True(SemanticVersion.Parse("1.0.0-beta.2") < SemanticVersion.Parse("1.0.0-beta.10"));
        Assert.True(SemanticVersion.Parse("1.0.0-beta.9") < SemanticVersion.Parse("1.0.0-beta.10"));
        Assert.True(SemanticVersion.Parse("1.0.0-beta.99") < SemanticVersion.Parse("1.0.0-beta.100"));
    }

    [Fact]
    public void SystemVersion_CannotEvenParse_ThePrereleaseSpelling()
    {
        // The reason SemanticVersion exists at all, asserted rather than asserted-in-a-comment.
        Assert.False(Version.TryParse("1.0.0-beta.1", out _));
        Assert.True(SemanticVersion.TryParse("1.0.0-beta.1", out _));
    }

    [Fact]
    public void LexicographicComparison_GetsPrereleaseOrdering_Backwards()
    {
        // The second reason: as text a prerelease sorts AFTER its own release.
        Assert.True(string.CompareOrdinal("1.0.0-beta.1", "1.0.0") > 0);
        Assert.True(SemanticVersion.Parse("1.0.0-beta.1") < SemanticVersion.Parse("1.0.0"));
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]          // more fields wins
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]     // numeric < alphanumeric
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void SpecExamples_Hold(string lower, string higher)
        => Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));

    [Fact]
    public void BuildMetadata_IsIgnoredForPrecedence()
    {
        Assert.True(SemanticVersion.Parse("1.0.0+aaa") == SemanticVersion.Parse("1.0.0+bbb"));
        Assert.True(SemanticVersion.Parse("1.0.0+aaa") == SemanticVersion.Parse("1.0.0"));
    }

    [Theory]
    [InlineData("v1.0.0-beta.1", "1.0.0-beta.1")]   // a git tag's leading v
    [InlineData("1.0", "1.0.0")]                     // the shorter VERSION spelling
    [InlineData("  1.2.3  ", "1.2.3")]
    public void Accepts(string text, string normalized)
        => Assert.Equal(normalized, SemanticVersion.Parse(text).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("01.0.0")]
    [InlineData("not-a-version")]
    public void Refuses(string? text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void TheRunningVersion_Parses()
    {
        // The VERSION file's actual spelling must be a version this comparer understands, or every
        // check silently offers nothing. Reads AppVersion, so it follows VERSION rather than a literal.
        Assert.True(SemanticVersion.TryParse(CircuitRF.Ui.AppVersion.Display, out SemanticVersion? v));
        Assert.NotNull(v);
    }
}
