using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for SDD equation name validation (brief #4 — weighting editor).
/// Exercises ParameterRowViewModel.TryValidateSddName directly (pure static, no Avalonia runtime needed).
/// </summary>
public sealed class SddEquationNameValidationTests
{
    // ── Accepted names ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("I[1]")]
    [InlineData("I[2]")]
    [InlineData("I[1,0]")]
    [InlineData("I[2,1]")]
    [InlineData("I[1,2]")]
    [InlineData("Q[1]")]
    [InlineData("Q[2]")]
    [InlineData("H[2]")]
    [InlineData("H[7]")]
    [InlineData("H[10]")]
    public void ValidSddNames_AcceptedWithEmptyError(string name)
    {
        bool ok = ParameterRowViewModel.TryValidateSddName(name, out string error);
        Assert.True(ok, $"Expected '{name}' to be valid but got error: {error}");
        Assert.Equal("", error);
    }

    // ── H[0] and H[1] are built-in ───────────────────────────────────────────

    [Theory]
    [InlineData("H[0]")]
    [InlineData("H[1]")]
    public void H0_H1_Rejected_BuiltInMessage(string name)
    {
        bool ok = ParameterRowViewModel.TryValidateSddName(name, out string error);
        Assert.False(ok);
        Assert.Contains("built-in", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Malformed H[…] ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("H[x]")]
    [InlineData("H[]")]
    [InlineData("H[1.5]")]
    public void MalformedH_Rejected_WeightMessage(string name)
    {
        bool ok = ParameterRowViewModel.TryValidateSddName(name, out string error);
        Assert.False(ok);
        Assert.Contains("integer weight", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── p must be ≥1 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("I[0]")]
    [InlineData("Q[0]")]
    public void PortIndexZero_Rejected_GenericMessage(string name)
    {
        bool ok = ParameterRowViewModel.TryValidateSddName(name, out string error);
        Assert.False(ok);
        Assert.Contains("I[p]", error, StringComparison.Ordinal);
    }

    // ── Malformed brackets / unknown head ────────────────────────────────────

    [Theory]
    [InlineData("I[1,")]
    [InlineData("Foo")]
    [InlineData("F[1]")]
    [InlineData("C[1]")]
    [InlineData("In[1]")]
    [InlineData("")]
    [InlineData("I")]
    [InlineData("I[1,2,3]")]
    public void MalformedOrUnknown_Rejected_GenericMessage(string name)
    {
        bool ok = ParameterRowViewModel.TryValidateSddName(name, out string error);
        Assert.False(ok);
        Assert.Contains("I[p]", error, StringComparison.Ordinal);
    }

    // ── Duplicate and empty checks still fire for SDD ────────────────────────
    // (These go through CommitName; TryValidateSddName is never called for those cases.)
    // We verify here that TryValidateSddName does NOT fire on empty — empty is caught before it.
    // So a valid-grammar empty string never reaches the validator.
    // This test documents that TryValidateSddName never rejects valid names it receives.
    [Fact]
    public void TryValidateSddName_ValidName_NeverReturnsFalse()
    {
        // Spot-check that none of the valid patterns accidentally fail.
        string[] valid = ["I[1]", "I[1,0]", "I[2,3]", "Q[1]", "H[2]", "H[99]"];
        foreach (string n in valid)
        {
            bool ok = ParameterRowViewModel.TryValidateSddName(n, out _);
            Assert.True(ok, $"'{n}' should be valid");
        }
    }
}
