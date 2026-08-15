// ================================================================
//  TerminationEditModelTests.cs — R8B §1.3
//
//  HarmonicaSetTerminationDialog is a Window and cannot be constructed headlessly in this suite, so
//  three previous rounds of "fix the Set Termination dialog" were verified only against hand-built
//  simulations of the handler shape — none of them could observe the real defect (an echo landing
//  outside the old `_loading` re-entrancy window). TerminationEditModel is the actual state machine the
//  dialog now runs, extracted with no Avalonia reference specifically so it can be driven directly.
// ================================================================

using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public class TerminationEditModelTests
{
    private static TerminationEditModel Model(double re = 0, double im = 0, double z0 = 50.0)
        => new(new Complex(re, im), z0);

    // ── The owner's case — typed character-by-character into the Z field ───────────────────────────

    [Theory]
    [InlineData("5")]
    [InlineData("50")]
    [InlineData("200")]
    [InlineData("12.5")]
    [InlineData("50-j10")]
    [InlineData("1e3")]
    public void TypingIntoZField_CharacterByCharacter_CommitsExactlyTheTypedValue(string full)
    {
        var m = Model();
        m.Editing = TerminationField.ZRealImag;

        string typed = "";
        foreach (char c in full)
        {
            typed += c;
            m.Edit(TerminationField.ZRealImag, typed);
        }

        Assert.True(HarmonicaReadoutFormatting.TryParse(full, ReadoutFormat.RealImaginary, out var expected));
        var edit = m.Commit();
        Assert.Null(edit.Gamma);
        Assert.NotNull(edit.Impedance);
        Assert.Equal(expected.Real,      edit.Impedance!.Value.Real,      precision: 9);
        Assert.Equal(expected.Imaginary, edit.Impedance!.Value.Imaginary, precision: 9);
    }

    [Fact]
    public void TheOwnersCase_5Then50_CommitsAsImpedance50()
    {
        var m = Model();
        m.Editing = TerminationField.ZRealImag;
        m.Edit(TerminationField.ZRealImag, "5");
        m.Edit(TerminationField.ZRealImag, "50");

        var edit = m.Commit();
        Assert.Equal(new Complex(50, 0), edit.Impedance);
        Assert.Null(edit.Gamma);
    }

    // ── The echo, made explicit — the test no previous round could write ───────────────────────────

    [Fact]
    public void AnEchoFromAnotherField_WhileEditingZ_DoesNotMoveTheModel()
    {
        var m = Model();
        m.Editing = TerminationField.ZRealImag;
        m.Edit(TerminationField.ZRealImag, "5");
        m.Edit(TerminationField.ZRealImag, "50");

        var zBefore = m.Z;
        var gammaBefore = m.Gamma;

        // The exact call an echo makes: the OTHER field's own TextChanged firing with its
        // just-reformatted text, after the window the old `_loading` flag guarded has closed.
        bool moved = m.Edit(TerminationField.GammaRealImag, "-0.818+j0.000");

        Assert.False(moved);
        Assert.Equal(zBefore, m.Z);
        Assert.Equal(gammaBefore, m.Gamma);
        Assert.False(m.LastEditWasGamma);

        var edit = m.Commit();
        Assert.Equal(new Complex(50, 0), edit.Impedance);
        Assert.Null(edit.Gamma);
    }

    [Fact]
    public void AnEchoFromAnotherField_WhileEditingGamma_DoesNotMoveTheModelOrFlipLastEditWasGamma()
    {
        var m = Model();
        m.Editing = TerminationField.GammaRealImag;
        m.Edit(TerminationField.GammaRealImag, "0.5+j0.2");
        var gammaBefore = m.Gamma;

        bool moved = m.Edit(TerminationField.ZRealImag, "37.5+j16.7");

        Assert.False(moved);
        Assert.Equal(gammaBefore, m.Gamma);
        Assert.True(m.LastEditWasGamma);
    }

    // ── TextFor must never be consulted on the field being edited ──────────────────────────────────

    [Fact]
    public void TextFor_OnTheEditingField_Throws()
    {
        var m = Model();
        m.Editing = TerminationField.ZRealImag;
        Assert.Throws<System.InvalidOperationException>(() => m.TextFor(TerminationField.ZRealImag));
    }

    [Fact]
    public void TextFor_OnANonEditingField_DoesNotThrow()
    {
        var m = Model();
        m.Editing = TerminationField.ZRealImag;
        _ = m.TextFor(TerminationField.GammaRealImag);
        _ = m.TextFor(TerminationField.GammaMagAngle);
    }

    // ── Deleting precision holds (the R6A follow-up's own case) ────────────────────────────────────

    [Fact]
    public void DeletedPrecision_SurvivesCommit_EvenThoughTextForMayReformatIt()
    {
        var m = Model();
        m.Editing = TerminationField.ZRealImag;
        m.Edit(TerminationField.ZRealImag, "158");
        m.Editing = null;

        // TextFor may reformat to fixed-decimal ("158.000 Ω") once nobody owns the field...
        string text = m.TextFor(TerminationField.ZRealImag);
        Assert.Contains("158", text);

        // ...but Commit() still carries the value the user actually typed, not a reparsed reformat.
        var edit = m.Commit();
        Assert.Equal(158.0, edit.Impedance!.Value.Real, precision: 9);
    }

    // ── Un-parseable in-progress text leaves the model exactly where it was ────────────────────────

    [Theory]
    [InlineData("1e")]     // dangling exponent — TryDouble rejects it, unlike a bare trailing 'j' (=1)
    [InlineData("")]
    [InlineData("-")]
    public void UnparseableInProgressText_ReturnsFalse_AndLeavesTheModelUnchanged(string bad)
    {
        var m = Model(re: 0.3, im: -0.2);
        m.Editing = TerminationField.GammaRealImag;
        var before = m.Gamma;

        bool moved = m.Edit(TerminationField.GammaRealImag, bad);

        Assert.False(moved);
        Assert.Equal(before, m.Gamma);
    }

    // ── PreviewImpedance folds into the model (R7A §1.3(c)) ─────────────────────────────────────────

    [Fact]
    public void ActiveGamma_PassesThroughUnclamped()
    {
        // Γ = -3 -> Z = -25 Ω. The OLD preview clamped |Γ| to 0.999 first, which would have shown the
        // Z of -0.999, not -25 — an active termination (|Γ| > 1) is ordinary here, not an error.
        var m = Model();
        m.Editing = TerminationField.GammaRealImag;
        m.Edit(TerminationField.GammaRealImag, "-3+j0");

        Assert.Equal(-25.0, m.Z.Real,      precision: 6);
        Assert.Equal(0.0,   m.Z.Imaginary, precision: 6);
    }
}
