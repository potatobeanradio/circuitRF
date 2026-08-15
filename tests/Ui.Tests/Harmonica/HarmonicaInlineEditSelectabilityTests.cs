// ================================================================
//  HarmonicaInlineEditSelectabilityTests.cs — brief-harmonicarf-r2b §4
//
//  R-h9r2-15  owner-reported: the inline text editor never engaged, because the value cell was a
//             SelectableTextBlock — its own double-tap (select-a-word) consumes the gesture before it
//             ever reaches the parent's DoubleTapped handler. Fixed by making only EDITABLE rows plain
//             TextBlock; every other row (General, MXP/MXE, headers) keeps SelectableTextBlock.
//  R-h9r2-16  the editor seeds with the value AND its unit, and pre-selects only the value.
//  R7C §1.1/§1.5  the unit moved into the row's LABEL cell; the seed is unit-free now and the box
//                 SelectAlls — there is no trailing unit token left to carve the selection around.
// ================================================================

using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaInlineEditSelectabilityTests
{
    private static string Source() =>
        ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    // ══ R-h9r2-15 — editable rows are NOT SelectableTextBlock ═══════════════════════════════════

    [Fact]
    public void BuildColumnRowShell_UsesPlainTextBlock_ForAnEditableRow_AndSelectable_ForEveryOther()
    {
        // brief-harmonicarf-r5 §2 — BuildColumnRow (rebuilt every SetItems call) split into
        // BuildColumnRowShell (build-once skeleton) + UpdateColumnRow (per-call value write). The
        // editable/selectable widget-choice rule this test pins moved to the shell, unchanged.
        string src = Source();

        int m = src.IndexOf("private Grid BuildColumnRowShell(", StringComparison.Ordinal);
        Assert.True(m >= 0, "Expected to find BuildColumnRowShell.");
        int mEnd = src.IndexOf("\n    /// <summary>Writes one row's CURRENT label", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("bool editable = item.Editable && hasCommit;", body, StringComparison.Ordinal);
        Assert.Contains("? new TextBlock", body, StringComparison.Ordinal);
        Assert.Contains(": new SelectableTextBlock", body, StringComparison.Ordinal);

        // The DoubleTapped wiring must be gated on the SAME `editable` flag the control choice used,
        // not re-derived — otherwise the two could disagree about which rows are editable.
        Assert.Contains("if (editable)", body, StringComparison.Ordinal);
        Assert.Contains("row.DoubleTapped += (_, _) =>", body, StringComparison.Ordinal);
    }

    // ══ R3C §1 — the Settings column's rows are ALSO plain TextBlock, never Selectable ═══════════

    [Fact]
    public void BuildSettingsColumnRow_UsesPlainTextBlockThroughout_NeverSelectableTextBlock()
    {
        string src = Source();

        int m = src.IndexOf("private Grid BuildSettingsColumnRow(", StringComparison.Ordinal);
        Assert.True(m >= 0, "Expected to find BuildSettingsColumnRow.");
        int mEnd = src.IndexOf("\n    private static void UpdateSettingsColumnRow", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        // Every row here is editable (R3C §1's whole point), so R-h9r2-15's rule applies to all of
        // it: label, value AND unit are plain TextBlock — none of them may be SelectableTextBlock, or
        // the row's own DoubleTapped (wired here too) would never engage.
        Assert.DoesNotContain("SelectableTextBlock", body, StringComparison.Ordinal);
        Assert.Contains("row.DoubleTapped += (_, _) =>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeneralRow_IsUntouched_StillSelectable_NoDoubleTapped()
    {
        string src = Source();

        int m = src.IndexOf("private static Control BuildGeneralRow(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>\n    /// What one Source/Load/MXP/MXE", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("new SelectableTextBlock", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleTapped", body, StringComparison.Ordinal);
    }

    // ══ R7C §1.1/§1.5 — the unit moved into the LABEL; the seed no longer carries one, and the box
    // now SelectAlls (there is no trailing unit token left to carve the selection around) ══════════

    [Fact]
    public void BeginInlineEdit_SeedsFromItemValueVerbatim()
    {
        string src = Source();
        int m = src.IndexOf("private void BeginInlineEdit(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private static double CalcInlineEditWidth", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("string pristine = currentDisplayValue;", body, StringComparison.Ordinal);
        Assert.Contains("Text              = pristine,", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BeginInlineEdit_SelectsAll_NowThatTheSeedCarriesNoUnitToCarveAround()
    {
        string src = Source();
        int m = src.IndexOf("private void BeginInlineEdit(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private static double CalcInlineEditWidth", m, StringComparison.Ordinal);
        string body = src[m..mEnd];

        Assert.Contains("box.SelectionStart = 0;", body, StringComparison.Ordinal);
        Assert.Contains("box.SelectionEnd   = pristine.Length;", body, StringComparison.Ordinal);
    }

    // EditSeedValue is private; invoked via reflection so the actual unit-stripping behaviour is
    // pinned, not merely its presence in source. It is what BeginInlineEdit's own `pristine` is
    // seeded from at every production call site (BuildColumnRowShell's DoubleTapped handler).
    private static string InvokeEditSeedValue(HarmonicaReadout item, Func<string, ReadoutFormat> formatFor)
    {
        var type = typeof(CircuitRF.Ui.Views.Harmonica.ReadoutStripView);
        var method = type.GetMethod("EditSeedValue", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [item, formatFor])!;
    }

    [Fact]
    public void EditSeedValue_StripsTheUnit_NowThatItLivesInTheLabel()
    {
        var z = new Complex(80, 10);
        var zRow = new HarmonicaReadout("ZL1", HarmonicaReadoutFormatting.FormatZ(z, ReadoutFormat.RealImaginary),
            "", ReadoutColumn.Load, IsComplex: true, Editable: true, RawValue: z, Unit: "Ω");
        Assert.Equal("80.000+j10.000", InvokeEditSeedValue(zRow, _ => ReadoutFormat.RealImaginary));

        // A Γ row (no unit at all, IsGamma) is unaffected — SplitUnit already left it alone.
        var gammaRow = zRow with { IsGamma = true, Unit = "" };
        string gammaSeed = InvokeEditSeedValue(gammaRow, _ => ReadoutFormat.RealImaginary);
        Assert.DoesNotContain("Ω", gammaSeed);
    }

    // ══ TryParse still tolerates a trailing unit defensively, even though the editor no longer
    // seeds one — a user could still type "80+j10 Ω" by hand. ═══════════════════════════════════

    [Fact]
    public void TryParse_StillAcceptsATrailingUnit_EvenThoughTheEditorNoLongerSeedsOne()
    {
        Assert.True(HarmonicaReadoutFormatting.TryParse("80+j10 Ω", ReadoutFormat.RealImaginary, out var z));
        Assert.Equal(new Complex(80, 10), z);

        // Round-trip: what BeginInlineEdit now seeds is the UNIT-FREE value half, and TryParse must
        // still accept it back unmodified.
        var (unitFree, _) = HarmonicaReadoutFormatting.SplitUnit(
            HarmonicaReadoutFormatting.FormatZ(new Complex(42, -7), ReadoutFormat.RealImaginary));
        Assert.True(HarmonicaReadoutFormatting.TryParse(unitFree, ReadoutFormat.RealImaginary, out var roundTripped));
        Assert.Equal(new Complex(42, -7), roundTripped);
    }
}
