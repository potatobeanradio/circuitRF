// ================================================================
//  HarmonicaSetTerminationDialogTests.cs — brief-harmonicarf-r6a §6, re-pointed by R8B §1.3
//
//  HarmonicaSetTerminationDialog is a Window and cannot be constructed headlessly in this suite (no
//  Avalonia platform). R8B §1.3 moved the entire parse/echo/ownership state machine out of the dialog
//  and into TerminationEditModel (see TerminationEditModelTests.cs), which now carries every case a
//  hand-built simulation used to stand in for. What's left here is what genuinely can only be checked
//  against the dialog's own source: the shape of the thin shell (ownership set on GotFocus, cleared
//  before the lost-focus reformat) and PreviewImpedance's own pass-through.
// ================================================================

using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public class HarmonicaSetTerminationDialogTests
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

    // ── R7A §1.3(c)/§1.4 — the live Z preview during a Γ edit must show an ACTIVE termination's real
    //    impedance, not the old |Γ| ≤ 0.999 clamp's saturated near-short ─────────────────────────────

    [Fact]
    public void PreviewImpedance_OfAnActiveGamma_IsTheRealImpedance_NotTheOldClamp()
    {
        // Owner's own worked example (brief §1.3(d)): Γ = -3 -> Z = -25 Ω. The OLD preview clamped
        // |Γ| to 0.999 first, which would have shown the Z of -0.999, not -25.
        var z = HarmonicaSetTerminationDialog.PreviewImpedance(new Complex(-3, 0), 50.0);
        Assert.Equal(-25.0, z.Real,      precision: 6);
        Assert.Equal(0.0,   z.Imaginary, precision: 6);
    }

    [Fact]
    public void PreviewImpedance_AgreesWithHarmonicaDataSetImpedanceOf_ForAnyGamma()
    {
        // No second formula — the preview is exactly ImpedanceOf, which already nudges only the
        // genuine Γ = 1 singularity (|1-Γ| < 1e-12) rather than clamping every |Γ| > 1.
        foreach (var g in new[] { new Complex(0.5, 0.2), new Complex(-3, 0), new Complex(1.0, 0.0), new Complex(-10, 4) })
        {
            var expected = HarmonicaDataSet.ImpedanceOf(g, 50.0);
            var actual   = HarmonicaSetTerminationDialog.PreviewImpedance(g, 50.0);
            Assert.Equal(expected.Real,      actual.Real,      precision: 9);
            Assert.Equal(expected.Imaginary, actual.Imaginary, precision: 9);
        }
    }

    // ── R8B §1.2 — the shell's own shape, pinned by source scan ─────────────────────────────────────

    [Fact]
    public void TheDialogHoldsNoStateOfItsOwn_OwnershipIsSetOnGotFocus()
    {
        string src = DialogSource();

        Assert.Contains("private TerminationEditModel _model", src, System.StringComparison.Ordinal);
        // No re-entrancy flag, no hand-held _z/_gamma/_lastEditWasGamma — that state lives in the
        // model. Checked as a field DECLARATION, not a bare substring — the class's own doc comment
        // legitimately mentions the OLD `_loading` flag by name while explaining what replaced it.
        Assert.DoesNotContain("bool _loading", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("_lastEditWasGamma;", src, System.StringComparison.Ordinal);

        Assert.Contains("_model.Editing = TerminationField.GammaRealImag", src, System.StringComparison.Ordinal);
        Assert.Contains("_model.Editing = TerminationField.GammaMagAngle", src, System.StringComparison.Ordinal);
        Assert.Contains("_model.Editing = TerminationField.ZRealImag",     src, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EveryField_ReformatsOnLostFocus_AndClearsOwnershipFirst()
    {
        string axaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Dialogs", "HarmonicaSetTerminationDialog.axaml"));

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            axaml, "LostFocus=\"OnFieldLostFocus\"").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            axaml, "GotFocus=\"On\\w+GotFocus\"").Count);

        string src = DialogSource();
        // Ownership is cleared BEFORE the reformat, so the reformat's own echo is disowned too — the
        // exact ordering that used to be missing and let a deleted-precision edit come back.
        int ownershipCleared = src.IndexOf("_model.Editing = null;", System.StringComparison.Ordinal);
        int reformatCall     = src.IndexOf("LoadFields(except: sender as TextBox);", System.StringComparison.Ordinal);
        Assert.True(ownershipCleared >= 0 && reformatCall >= 0 && ownershipCleared < reformatCall);
    }
}
