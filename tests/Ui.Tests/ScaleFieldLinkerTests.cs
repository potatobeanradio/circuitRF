using System;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Tests;

// Third report on this bug (docs/sonnet-briefs/brief-L1h-fix-scale-dialog-width.md): typing a
// Width/Height directly into the "Scale…" dialog was being silently corrupted (e.g. "400" became
// "400.9001"). Two prior fix attempts targeted the exact-factor math — which was already correct —
// while the real defect was policy living in ScaleDialog.axaml.cs, the one layer that cannot be
// constructed in this project's headless test suite (a Window subclass). This file pins the fix now
// that the policy has moved INTO ScaleFieldLinker: Edit(field, text) records which field the caller
// just committed as authoritative, and DisplayFor(field) returns null for it — never written back —
// which is the invariant that makes the whole class of bug impossible, not just unlikely.
//
// Fixture: 3_728_000 / 1_000_000 DBU is deliberately chosen so the required factor (400/3728) needs
// more significant digits than the 4-decimal display can hold — exactly the shape of input that
// triggered the original bug.

public class ScaleFieldLinkerTests
{
    private static ScaleFieldLinker Fixture() =>
        new(origWidthDbu: 3_728_000, origHeightDbu: 1_000_000, LayoutUnit.Um, dbuPerMicron: 1000);

    // Gate 1: typed width survives — the headline.
    [Fact]
    public void TypedWidth_Survives_AuthoritativeFieldIsNeverWrittenBack()
    {
        var linker = Fixture();

        Assert.True(linker.Edit(ScaleField.Width, "400"));

        Assert.Equal(ScaleField.Width, linker.AuthoritativeField);
        Assert.Null(linker.DisplayFor(ScaleField.Width)); // authoritative — must not be written back
        Assert.Equal(400_000.0 / 3_728_000.0, linker.FactorX, precision: 12);
        Assert.Equal(400_000, linker.ScaledWidthDbu);
    }

    // Gate 2: refresh is idempotent — repeatedly reading every OTHER field's display string and
    // feeding it back through the dialog's own no-op loop (nothing here ever calls Edit again) must
    // never drift FactorX.
    [Fact]
    public void Refresh_IsIdempotent_HundredReadsLeaveFactorXBitIdentical()
    {
        var linker = Fixture();
        linker.Edit(ScaleField.Width, "400");
        double factorAfterEdit = linker.FactorX;

        for (int i = 0; i < 100; i++)
        {
            _ = linker.DisplayFor(ScaleField.FactorX);
            _ = linker.DisplayFor(ScaleField.FactorY);
            _ = linker.DisplayFor(ScaleField.Width);
            _ = linker.DisplayFor(ScaleField.Height);
        }

        Assert.Equal(factorAfterEdit, linker.FactorX);
        Assert.Equal("400", linker.WidthText);
    }

    // Gate 3: no round-trip through display text — stated as a test, not just prose. The dialog never
    // feeds FactorText/WidthText back through Edit for the field that produced it; DisplayFor returning
    // null for the authoritative field is what makes that structurally impossible.
    [Fact]
    public void AuthoritativeField_DisplayForReturnsNull_NoRoundTripThroughDisplayTextIsPossible()
    {
        var linker = Fixture();
        linker.Edit(ScaleField.Width, "400");
        Assert.Null(linker.DisplayFor(ScaleField.Width));

        linker.Edit(ScaleField.Height, "250");
        Assert.Null(linker.DisplayFor(ScaleField.Height));
        // Width is no longer authoritative — its display string is available again for the OTHER boxes.
        Assert.NotNull(linker.DisplayFor(ScaleField.Width));
    }

    // Gate 4: uniform cross-assignment regression (R-fix-5) — the specific bug, reproduced via its
    // realistic trigger. With LostFocus/Enter (R-fix-1) replacing TextChanged, a stray commit can still
    // happen legitimately: the user types Width, then Tabs through Height without touching it. Height's
    // LostFocus fires with its box holding exactly what the last refresh wrote — DisplayFor(Height).
    // Feeding that back through Edit must be a no-op: nothing was actually typed, so nothing may be
    // re-derived, and FactorX (set by the genuine Width edit) must stay untouched — not approximately,
    // bit-for-bit. Without R-fix-5 this fails: the old TrySetHeightText unconditionally re-derives
    // FactorY from the (already-rounded) text and, because Uniform is on, overwrites FactorX with it.
    [Fact]
    public void Uniform_TabbingThroughHeightUnedited_CommitsItsOwnDisplayText_IsANoOp_FactorXUntouched()
    {
        var linker = Fixture();
        Assert.True(linker.IsUniform);

        linker.Edit(ScaleField.Width, "400");
        double factorXAfterWidthEdit = linker.FactorX;
        string? heightDisplay = linker.DisplayFor(ScaleField.Height);
        Assert.NotNull(heightDisplay);

        // Tab into Height and back out without typing anything: LostFocus commits whatever the box
        // already holds, which is exactly heightDisplay.
        bool accepted = linker.Edit(ScaleField.Height, heightDisplay!);

        Assert.True(accepted); // not rejected — just a no-op, not a parse failure
        Assert.Equal(factorXAfterWidthEdit, linker.FactorX); // bit-identical — nothing was re-derived
        Assert.Equal(ScaleField.Width, linker.AuthoritativeField); // Width is still the one the user edited
    }

    [Fact]
    public void TypedHeight_IsRespectedExactly()
    {
        var linker = Fixture();

        Assert.True(linker.Edit(ScaleField.Height, "250"));

        Assert.Equal("250", linker.HeightText);
        Assert.Equal(250_000, linker.ScaledHeightDbu);
    }

    [Fact]
    public void Uniform_EditingWidth_UpdatesHeightProportionally_BothExact()
    {
        var linker = Fixture(); // IsUniform = true by default

        linker.Edit(ScaleField.Width, "400");

        Assert.Equal(linker.FactorX, linker.FactorY);
        long expectedHeightDbu = (long)Math.Round(1_000_000 * (400_000.0 / 3_728_000.0));
        Assert.Equal(expectedHeightDbu, linker.ScaledHeightDbu);
    }

    [Fact]
    public void NonUniform_EditingWidth_LeavesHeightFactorUntouched()
    {
        var linker = Fixture();
        linker.IsUniform = false;
        linker.Edit(ScaleField.Height, "300"); // establish a distinct FactorY first
        double factorYBefore = linker.FactorY;

        linker.Edit(ScaleField.Width, "400");

        Assert.Equal(factorYBefore, linker.FactorY); // untouched by the width edit
        Assert.Equal("400", linker.WidthText);
    }

    [Fact]
    public void TypedFactor_IsUsedDirectly_NoRoundingArtifact()
    {
        var linker = Fixture();

        Assert.True(linker.Edit(ScaleField.FactorX, "1.033203125"));

        Assert.Equal(1.033203125, linker.FactorX, precision: 12);
        Assert.Equal("1.0332", linker.FactorText);
        Assert.NotEqual(1.0332, linker.FactorX);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("-5")]
    [InlineData("0")]
    public void InvalidOrNonPositiveInput_IsRejected_StateUnchanged(string text)
    {
        var linker = Fixture();
        linker.Edit(ScaleField.Width, "400"); // establish a known-good state first
        double factorBefore = linker.FactorX;
        ScaleField? authoritativeBefore = linker.AuthoritativeField;

        bool accepted = linker.Edit(ScaleField.Width, text);

        Assert.False(accepted);
        Assert.Equal(factorBefore, linker.FactorX); // rejected — nothing changed
        Assert.Equal(authoritativeBefore, linker.AuthoritativeField);
    }
}
