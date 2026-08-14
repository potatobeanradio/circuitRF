// ================================================================
//  HarmonicaInlineEditSelectabilityTests.cs — brief-harmonicarf-r2b §4
//
//  R-h9r2-15  owner-reported: the inline text editor never engaged, because the value cell was a
//             SelectableTextBlock — its own double-tap (select-a-word) consumes the gesture before it
//             ever reaches the parent's DoubleTapped handler. Fixed by making only EDITABLE rows plain
//             TextBlock; every other row (General, MXP/MXE, headers) keeps SelectableTextBlock.
//  R-h9r2-16  the editor seeds with the value AND its unit, and pre-selects only the value.
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
    public void BuildColumnRow_UsesPlainTextBlock_ForAnEditableRow_AndSelectable_ForEveryOther()
    {
        string src = Source();

        int m = src.IndexOf("private Control BuildColumnRow(", StringComparison.Ordinal);
        Assert.True(m >= 0, "Expected to find BuildColumnRow.");
        int mEnd = src.IndexOf("\n    private static ContextMenu BuildFormatMenu", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("bool editable = item.Editable && onCommitEdit is not null;", body, StringComparison.Ordinal);
        Assert.Contains("? new TextBlock", body, StringComparison.Ordinal);
        Assert.Contains(": new SelectableTextBlock", body, StringComparison.Ordinal);

        // The DoubleTapped wiring must be gated on the SAME `editable` flag the control choice used,
        // not re-derived — otherwise the two could disagree about which rows are editable.
        Assert.Contains("if (editable)", body, StringComparison.Ordinal);
        Assert.Contains("pair.DoubleTapped += (_, _) =>", body, StringComparison.Ordinal);
    }

    // ══ R3C §1 — the Settings column's rows are ALSO plain TextBlock, never Selectable ═══════════

    [Fact]
    public void BuildSettingsColumnRow_UsesPlainTextBlockThroughout_NeverSelectableTextBlock()
    {
        string src = Source();

        int m = src.IndexOf("private StackPanel BuildSettingsColumnRow(", StringComparison.Ordinal);
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
        int mEnd = src.IndexOf("\n    private Control BuildColumnRow", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("new SelectableTextBlock", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleTapped", body, StringComparison.Ordinal);
    }

    // ══ R-h9r2-16 — seed with the unit, select only the value ═══════════════════════════════════

    [Fact]
    public void BeginInlineEdit_SeedsFromItemValueVerbatim_UnitIncluded()
    {
        string src = Source();
        int m = src.IndexOf("private void BeginInlineEdit(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private static int ValueSelectionLength", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("string pristine = currentDisplayValue;", body, StringComparison.Ordinal);
        Assert.Contains("Text              = pristine,", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BeginInlineEdit_SelectsOnlyTheValue_NotSelectAll()
    {
        string src = Source();
        int m = src.IndexOf("private void BeginInlineEdit(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private static int ValueSelectionLength", m, StringComparison.Ordinal);
        string body = src[m..mEnd];

        Assert.DoesNotContain("box.SelectAll();", body, StringComparison.Ordinal);
        Assert.Contains("box.SelectionStart = 0;", body, StringComparison.Ordinal);
        Assert.Contains("box.SelectionEnd   = ValueSelectionLength(pristine);", body, StringComparison.Ordinal);
    }

    // ValueSelectionLength is private; invoked via reflection so the actual boundary math is pinned
    // rather than merely its presence in source.
    private static int InvokeValueSelectionLength(string text)
    {
        var type = typeof(CircuitRF.Ui.Views.Harmonica.ReadoutStripView);
        var method = type.GetMethod("ValueSelectionLength",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)method.Invoke(null, [text])!;
    }

    [Theory]
    [InlineData("80+j10 Ω", 6)]           // Z row — select the value, leave " Ω" alone
    [InlineData("0.35-j0.2", 9)]          // Γ row — no unit, select everything
    [InlineData("0.5∠30°", 7)]            // magnitude/angle Γ — no space, select everything
    [InlineData("12.5∠-45° Ω", 9)]        // magnitude/angle Z — select up to the unit's space
    public void ValueSelectionLength_ExcludesOnlyATrailingUnit(string text, int expected)
    {
        Assert.Equal(expected, InvokeValueSelectionLength(text));
    }

    // ══ TryParse already tolerates the trailing unit the editor now always seeds ═════════════════

    [Fact]
    public void TryParse_AlreadyAcceptsTheUnitBackFromTheEditor_NoSecondStripStepNeeded()
    {
        Assert.True(HarmonicaReadoutFormatting.TryParse("80+j10 Ω", ReadoutFormat.RealImaginary, out var z));
        Assert.Equal(new Complex(80, 10), z);

        // Round-trip: what BeginInlineEdit seeds is exactly what FormatZ produced, and TryParse must
        // accept it back unmodified — the editor never needs a call-site strip-the-unit step.
        string formatted = HarmonicaReadoutFormatting.FormatZ(new Complex(42, -7), ReadoutFormat.RealImaginary);
        Assert.True(HarmonicaReadoutFormatting.TryParse(formatted, ReadoutFormat.RealImaginary, out var roundTripped));
        Assert.Equal(new Complex(42, -7), roundTripped);
    }
}
