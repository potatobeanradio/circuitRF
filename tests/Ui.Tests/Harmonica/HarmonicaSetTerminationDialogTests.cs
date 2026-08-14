// ================================================================
//  HarmonicaSetTerminationDialogTests.cs — brief-harmonicarf-r6a §6
//
//  Owner-reported: typing 200 into L1's Z field committed 190 Ω. HarmonicaSetTerminationDialog is a
//  Window and cannot be constructed headlessly in this suite (no Avalonia platform — the same
//  constraint every other Window-hosted dialog in this repo is tested under; see
//  HarmonicaAppMenuInjectorTests' own file header for the identical reason on NativeMenuItem). So this
//  drives the dialog's ACTUAL parse/format functions (HarmonicaReadoutFormatting — the one real place
//  a Z/Γ readout is parsed and formatted) through hand-built simulations of the OLD and NEW handler
//  shapes, rather than a real TextBox.
// ================================================================

using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public class HarmonicaSetTerminationDialogTests(ITestOutputHelper output)
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    private static string DialogSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Ui", "Views", "Dialogs", "HarmonicaSetTerminationDialog.axaml.cs"));

    // ── §6.1 — the mechanism, reproduced against the REAL parse/format functions ────────────────────
    //
    // Model: on every keystroke, the OLD LoadFields() unconditionally reformatted and rewrote ALL
    // THREE boxes — including the one being typed in. The next keystroke is inserted at whatever
    // CaretIndex Avalonia leaves after that programmatic Text set; the one documented, common Avalonia
    // behaviour (CaretIndex is NOT reset on a same-thread Text write, only clamped to the new length)
    // is simulated here. Under exactly that model, the owner's specific "200" (typed into an EMPTY,
    // freshly-selected box) does NOT corrupt — it is the SHORTER, luckier case, confirmed below rather
    // than assumed. What DOES corrupt, reproducibly, under the identical mechanism: typing into a box
    // that already carries text (replacing a selection, or resuming mid-edit), and any exponent-form
    // input, where the reformatted string's structure shifts under the caret. This is the same defect
    // class the owner hit — a rewrite-on-every-keystroke racing the caret — even though this file
    // cannot pin the literal "200 → 190" figure without a live TextBox's own CaretIndex semantics
    // (stated plainly, per this brief's own instruction, rather than a fabricated repro).

    private static (string finalText, Complex? lastCommitted) SimulateOldAlgorithm(
        string keystrokes, string startText = "", int startCaret = 0)
    {
        string text = startText;
        int caret = startCaret;
        Complex? lastGood = null;
        foreach (char c in keystrokes)
        {
            text = text.Insert(caret, c.ToString());
            caret++;
            if (HarmonicaReadoutFormatting.TryParse(text, ReadoutFormat.RealImaginary, out var z))
            {
                lastGood = z;
                // pad:false — this simulates what the OLD algorithm would rewrite a live TextBox with,
                // and R6C §4.2's fixed-width padding was never meant to reach an editable box (see
                // HarmonicaSetTerminationDialog.LoadFields' own pad:false, and ReadoutStripView's
                // EditSeedValue) — the padded form is a strip-column-only concern.
                text = HarmonicaReadoutFormatting.FormatZ(z, ReadoutFormat.RealImaginary, pad: false);
                caret = System.Math.Min(caret, text.Length);   // CaretIndex clamped, not reset
            }
        }
        return (text, lastGood);
    }

    [Fact]
    public void OldAlgorithm_TypingIntoAFreshlySelectedEmptyBox_HappensNotToCorrupt200()
    {
        // The owner's own reported keystrokes, from an empty (selected-then-replaced) box — the
        // luckiest case for the old algorithm, confirmed rather than assumed.
        var (finalText, committed) = SimulateOldAlgorithm("200");
        output.WriteLine($"'200' from empty -> '{finalText}', committed {committed}");
        Assert.Equal(new Complex(200, 0), committed);

        // The exact "200 -> 190" figure did not reproduce under this caret model — stated plainly
        // rather than forced. What reproduces instead is the SAME mechanism corrupting other, equally
        // realistic keystroke sequences (below), which is why the fix removes the mechanism entirely
        // rather than chasing this one number.
    }

    [Fact]
    public void OldAlgorithm_TypingOverExistingText_CorruptsTheValue()
    {
        // Resuming mid-edit (caret NOT at 0, box NOT empty) — e.g. the user clicked partway into the
        // Z field showing a prior termination and typed "200" without selecting first.
        var (finalText, committed) = SimulateOldAlgorithm("200", startText: "50+j0 Ω", startCaret: 2);
        output.WriteLine($"'200' typed at caret 2 into '50+j0 Ω' -> '{finalText}', committed {committed}");

        // Real, computed corruption: NOT 200+j0.
        Assert.NotEqual(new Complex(200, 0), committed);
    }

    [Fact]
    public void OldAlgorithm_AnExponentOrImaginaryTerm_CanBeSilentlyDroppedOrMangled()
    {
        var (text1, c1) = SimulateOldAlgorithm("-25+j40");
        output.WriteLine($"'-25+j40' -> '{text1}', committed {c1}");
        Assert.NotEqual(new Complex(-25, 40), c1);   // the imaginary term does not survive

        var (text2, c2) = SimulateOldAlgorithm("1e3");
        output.WriteLine($"'1e3' -> '{text2}', committed {c2}");
        Assert.NotEqual(new Complex(1000, 0), c2);   // the exponent does not survive either
    }

    // ── §6.2 — the fix's own handler contract, simulated the SAME way but WITHOUT the rewrite ───────
    //
    // Mirrors LoadFields(except:)/On*Changed exactly: the box being typed in is NEVER programmatically
    // rewritten, so keystrokes always land in the box's own accumulating text — no caret model needed,
    // because nothing else ever touches it until commit.

    private static Complex SimulateFixedAlgorithm(string keystrokes)
    {
        string text = "";
        Complex last = default;
        foreach (char c in keystrokes)
        {
            text += c;   // the box's own text — never rewritten by the handler while it has focus
            if (HarmonicaReadoutFormatting.TryParse(text, ReadoutFormat.RealImaginary, out var z))
                last = z;
            // An un-parseable in-progress prefix (e.g. "20" then "0+j" mid-type) simply leaves `last`
            // at its previous value and the box's text untouched — exactly LoadFields' own "refuse
            // silently, leave the text alone" contract for bad in-progress input.
        }
        return last;
    }

    [Theory]
    [InlineData("200",              200, 0)]
    [InlineData("200+j0",           200, 0)]
    [InlineData("-25+j40",          -25, 40)]
    [InlineData("1e3",              1000, 0)]
    public void FixedAlgorithm_TypedCharacterByCharacter_CommitsExactlyTheTypedValue(
        string keystrokes, double expectedRe, double expectedIm)
    {
        var committed = SimulateFixedAlgorithm(keystrokes);
        Assert.Equal(expectedRe, committed.Real,      precision: 9);
        Assert.Equal(expectedIm, committed.Imaginary, precision: 9);
    }

    [Fact]
    public void FixedAlgorithm_AcceptsSpacesAndTheOhmSuffix_TypedCharacterByCharacter()
    {
        // "200 + j0 Ω" — TryParse strips a trailing Ω and TryParseRectangular strips spaces, so this
        // must commit identically to "200+j0" even though it is a much longer, messier keystroke run.
        var committed = SimulateFixedAlgorithm("200 + j0 Ω");
        Assert.Equal(200.0, committed.Real,      precision: 9);
        Assert.Equal(0.0,   committed.Imaginary, precision: 9);
    }

    [Fact]
    public void BareRealNumber_ParsesAs_ValuePlusJZero_NoChangeNeeded()
    {
        // §6.2's own note: TryParseRectangular already handles a bare "200" (no +j0, no Ω) as
        // 200 + j0 — this is a test pinning that existing behaviour, not a change.
        Assert.True(HarmonicaReadoutFormatting.TryParse("200", ReadoutFormat.RealImaginary, out var z));
        Assert.Equal(new Complex(200, 0), z);
    }

    // ── the fix's own shape, pinned by source scan ───────────────────────────────────────────────

    [Fact]
    public void LoadFields_NeverRewritesTheBoxCurrentlyBeingEdited()
    {
        string src = DialogSource();

        Assert.Contains("private void LoadFields(TextBox? except = null)", src, System.StringComparison.Ordinal);
        Assert.Contains("if (!ReferenceEquals(except, GammaRealImagBox))", src, System.StringComparison.Ordinal);
        Assert.Contains("if (!ReferenceEquals(except, GammaMagAngleBox))", src, System.StringComparison.Ordinal);
        Assert.Contains("if (!ReferenceEquals(except, ZRealImagBox))",     src, System.StringComparison.Ordinal);

        // Each live-edit handler passes ITSELF as the exclusion — never a bare LoadFields() while a
        // box could still have focus mid-keystroke.
        Assert.Contains("LoadFields(except: ZRealImagBox)",    src, System.StringComparison.Ordinal);
        Assert.Contains("LoadFields(except: edited)",          src, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EveryField_ReformatsOnLostFocus_NotOnEveryKeystroke()
    {
        string axaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Dialogs", "HarmonicaSetTerminationDialog.axaml"));

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            axaml, "LostFocus=\"OnFieldLostFocus\"").Count);

        string src = DialogSource();
        Assert.Contains("private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => LoadFields();",
            src, System.StringComparison.Ordinal);
    }
}
