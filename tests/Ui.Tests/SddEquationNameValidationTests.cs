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
    [InlineData("F[1]")]     // a free-form implicit equation — the factory refuses it by name
    [InlineData("In[1]")]    // a noise entry, silently skipped by the factory: not a slot
    [InlineData("")]
    [InlineData("I[1,2,3]")]
    [InlineData("_v1")]      // an injected port voltage — a constant shadowing one is not allowed
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

    // ── The three slots the engine supports that this validator used to refuse ────
    //
    // V[p], C[n]/Cport[n] and a plainly-named constant are all read by
    // ComponentModelFactory.CreateSddModel, and were rejected here — so a dialog refused names its
    // own engine runs. The "Add Equation…" picker creates exactly these, which is what made the
    // gap visible (owner report, 2026-09-02).

    [Theory]
    [InlineData("V[1]")]
    [InlineData("V[2]")]
    [InlineData("C[1]")]
    [InlineData("Cport[1]")]
    [InlineData("Param1")]
    [InlineData("W")]
    [InlineData("Idss_scale")]
    public void SlotsTheFactoryReads_AreAccepted(string name)
    {
        bool ok = ParameterRowViewModel.TryValidateSddName(name, out string error);
        Assert.True(ok, $"Expected '{name}' to be valid but got error: {error}");
    }

    // ── A port index beyond the port count is refused HERE, not at Run ────────
    //
    // The alternative is the factory's own "references port 3 but only 2 port(s) of nets were
    // given" — the same fact, learned a simulation later.

    [Theory]
    [InlineData("I[3]")]
    [InlineData("I[3,0]")]
    [InlineData("Q[3]")]
    [InlineData("V[3]")]
    public void PortBeyondPortCount_Rejected_NamesTheCount(string name)
    {
        bool ok = ParameterRowViewModel.TryValidateSddName(name, out string error, portCount: 2);
        Assert.False(ok);
        Assert.Contains("2 port", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PortWithinPortCount_Accepted()
    {
        Assert.True(ParameterRowViewModel.TryValidateSddName("I[2,1]", out _, portCount: 2));
        Assert.True(ParameterRowViewModel.TryValidateSddName("V[2]",   out _, portCount: 2));
    }

    /// <summary>A control reference indexes OTHER instances, so the port count says nothing
    /// about it — C[3] on a 2-port SDD is ordinary.</summary>
    [Fact]
    public void ControlIndex_IsNotBoundedByPortCount()
        => Assert.True(ParameterRowViewModel.TryValidateSddName("C[3]", out _, portCount: 2));
}
