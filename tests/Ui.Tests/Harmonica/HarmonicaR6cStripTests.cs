// ================================================================
//  HarmonicaR6cStripTests.cs  —  brief-harmonicarf-r6c-readout-strip-layout-and-stability
//
//  §1  the 2 x 4 chunk grid (pinned in HarmonicaR3cStripTests.ColumnsGrid_PlacesTheEightChunks...).
//  §2  Intrinsic VDS / Intrinsic IDS — two new chunks, read from the already-published V_intr/I_intr
//      cubes, magnitude ∠ angle by default.
//  §3  Settings label renames (compr -> "Compression:", f0 -> "Freq:", K -> "Harmonic Order:").
//  §4  fixed-DECIMAL formatting (R-hui-2) — every produced string for a given row TYPE holds the same
//      decimal-place count regardless of the row's own value; column stability is the reserved
//      control WIDTH's job, not text padding, so a complex row's real/imaginary parts sit with no gap.
//  §5  per-chunk Copy — one ContextMenu per chunk, sharing HarmonicaClipboard.RowsText with the
//      existing Edit > Copy Readouts.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR6cStripTests(ITestOutputHelper output)
{
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

    private static HarmonicaViewModel NewSolvedVm()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Null(vm.SolveError);
        return vm;
    }

    // ══ §2 — the two new intrinsic chunks ═══════════════════════════════════════════════════════

    [Fact]
    public void IntrinsicColumns_CarryOneRowPerHarmonic_PlusAHeader_ComplexAndReadOnly()
    {
        var vm = NewSolvedVm();
        int k = vm.Model.Settings.HarmonicCount;

        var vds = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.IntrinsicVds).ToArray();
        var ids = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.IntrinsicIds).ToArray();

        // The default document's DUT is a two-port SDD (HarmonicaViewModel's own constructor), which
        // needs no mapping (IntrinsicPortMap.TwoPort) — so the intrinsic plane IS located, and both
        // chunks carry a header plus K+1 harmonic rows (DC..Kf0), never the "not located" fallback.
        Assert.Equal(k + 2, vds.Length);   // header + (K+1) harmonics
        Assert.Equal(k + 2, ids.Length);

        // Owner: the unit lives once, in the header, in brackets — not repeated on every value row.
        Assert.Equal("Intrinsic VDS (V)", vds[0].Label);
        Assert.Equal("", vds[0].Value);   // header row, same shape as every other chunk's own header
        Assert.Equal("Intrinsic IDS (A)", ids[0].Label);

        Assert.Equal("DC", vds[1].Label);
        for (int h = 1; h <= k; h++)
            Assert.Equal($"{h}f0", vds[h + 1].Label);

        foreach (var row in vds.Skip(1).Concat(ids.Skip(1)))
        {
            Assert.True(row.IsComplex);
            Assert.False(row.Editable);   // "a consequence of the solve", never something the user may type into
            Assert.True(row.RawValue.HasValue);
            Assert.NotNull(row.FormatKey);
        }

        output.WriteLine(string.Join(", ", vds.Select(r => $"{r.Label}={r.Value}")));
    }

    [Fact]
    public void IntrinsicColumns_DefaultToMagnitudeAngle_ButCanStillBeToggled()
    {
        // The owner is explicit that these render magnitude ∠ angle — HarmonicaViewModel.
        // ReadoutFormatLookup (via HarmonicaReadoutFormatting.DefaultReadoutFormat) special-cases the
        // VDSi./IDSi. key namespace to default there, while every other row keeps real/imaginary.
        Assert.Equal(ReadoutFormat.MagnitudeAngle, HarmonicaReadoutFormatting.DefaultReadoutFormat("VDSi.1"));
        Assert.Equal(ReadoutFormat.MagnitudeAngle, HarmonicaReadoutFormatting.DefaultReadoutFormat("IDSi.0"));
        Assert.Equal(ReadoutFormat.RealImaginary, HarmonicaReadoutFormatting.DefaultReadoutFormat("S1.Z"));
        Assert.Equal(ReadoutFormat.RealImaginary, HarmonicaReadoutFormatting.DefaultReadoutFormat("MXP.Zin"));

        var vm = NewSolvedVm();
        var row = vm.Frame.Readouts.First(r => r.Column == ReadoutColumn.IntrinsicVds && r.Label == "DC");
        Assert.Contains('∠', row.Value);   // magnitude ∠ angle, not real+jimag, as the DEFAULT
    }

    [Fact]
    public void FormatKey_IdentifiesEachIntrinsicRow_UniquelyByHarmonic()
    {
        var vds1 = new HarmonicaReadout("1f0", "", "", ReadoutColumn.IntrinsicVds, IsComplex: true, Band: 1);
        var vds2 = new HarmonicaReadout("2f0", "", "", ReadoutColumn.IntrinsicVds, IsComplex: true, Band: 2);
        var ids1 = new HarmonicaReadout("1f0", "", "", ReadoutColumn.IntrinsicIds, IsComplex: true, Band: 1);

        Assert.Equal("VDSi.1", vds1.FormatKey);
        Assert.Equal("VDSi.2", vds2.FormatKey);
        Assert.Equal("IDSi.1", ids1.FormatKey);
        Assert.NotEqual(vds1.FormatKey, ids1.FormatKey);   // VDS and IDS at the SAME harmonic format independently
    }

    // ══ §3 — the Settings label renames (keys unchanged) ════════════════════════════════════════

    [Fact]
    public void SettingsLabels_AreRenamed_ButTheirKeysAndValuesSurvive()
    {
        var vm = new HarmonicaViewModel();
        var byKey = vm.Inputs.ToDictionary(i => i.Key);

        Assert.Equal("P-xdB",        byKey[HarmonicaInputs.KeyCompression].Label);
        Assert.Equal("Freq:",        byKey[HarmonicaInputs.KeyFrequency].Label);
        Assert.Equal("HB Order:",    byKey[HarmonicaInputs.KeyHarmonicCount].Label);

        // Vds/Vgs/Z0 are untouched — only the three named labels moved.
        Assert.Equal("Vds", byKey[HarmonicaInputs.KeyVds].Label);
        Assert.Equal("Vgs", byKey[HarmonicaInputs.KeyVgs].Label);
    }

    // ══ §4.1/§4.3 (R-hui-2/R-hui-4) — fixed-DECIMAL formatting; column stability moved to the GRID ══
    //
    // HarmonicaReadoutFormatting no longer pads: a row's string length is no longer forced constant by
    // stuffing leading spaces into the TEXT (that scheme is what produced the "x+j     y" gap the owner
    // asked to remove). Column stability is now a SharedSizeGroup'd Grid column's job (ReadoutStripView
    // — ReservedValueChars is gone), sized to the widest content each chunk actually has. What
    // FixedWidth must still guarantee: the same DECIMAL PLACE count regardless of magnitude (10.123 ->
    // 10.120 must not move a digit), and a bounded exponent fallback for a pathologically large value.

    private static readonly double[] SweepMagnitudes =
        [0.001, 0.01, 0.1, 1, 9.999, 10.001, 50, 100, 1000, 5000];

    [Fact]
    public void FixedWidth_NeverPads_AndAlwaysHoldsTheDecimalPlaceCount()
    {
        var quantities = new (int Decimals, int Budget)[]
        {
            (HarmonicaReadoutFormatting.ComplexPartDecimals, HarmonicaReadoutFormatting.ComplexPartBudget),
            (HarmonicaReadoutFormatting.ComplexMagDecimals,  HarmonicaReadoutFormatting.ComplexMagBudget),
            (HarmonicaReadoutFormatting.AngleDecimals,       HarmonicaReadoutFormatting.AngleBudget),
            (HarmonicaReadoutFormatting.DbmDecimals,         HarmonicaReadoutFormatting.DbmBudget),
            (HarmonicaReadoutFormatting.DbDecimals,          HarmonicaReadoutFormatting.DbBudget),
            (HarmonicaReadoutFormatting.PercentDecimals,     HarmonicaReadoutFormatting.PercentBudget),
            (HarmonicaReadoutFormatting.WattDecimals,        HarmonicaReadoutFormatting.WattBudget),
            (HarmonicaReadoutFormatting.DegreeDecimals,      HarmonicaReadoutFormatting.DegreeBudget),
        };

        foreach (var (decimals, budget) in quantities)
        foreach (double mag in SweepMagnitudes)
        foreach (double value in new[] { mag, -mag })
        {
            string s = HarmonicaReadoutFormatting.FixedWidth(value, decimals, budget);
            Assert.False(s.StartsWith(' '), $"FixedWidth({value}, {decimals}, {budget}) = '{s}' is padded");
            int dot = s.IndexOf('.');
            int actualDecimals = dot < 0 ? 0 : s.Length - dot - 1;
            Assert.True(actualDecimals == decimals,
                $"FixedWidth({value}, {decimals}, {budget}) = '{s}' has {actualDecimals} decimals, expected {decimals}");
        }
    }

    [Fact]
    public void FixedWidth_SameDigitCount_ProducesTheSameLength_ButADigitCountChangeMayGrow()
    {
        // The owner's own tolerance: 10.123 -> 10.120 (same digit count) must not move; 9 -> 10 (a
        // digit-count change) MAY — and does, since nothing pads it back out any more.
        string a = HarmonicaReadoutFormatting.FixedWidth(10.123, 3, 10);
        string b = HarmonicaReadoutFormatting.FixedWidth(10.120, 3, 10);
        Assert.Equal(a.Length, b.Length);

        string nine = HarmonicaReadoutFormatting.FixedWidth(9.99, 2, 9);
        string ten  = HarmonicaReadoutFormatting.FixedWidth(10.01, 2, 9);
        Assert.True(ten.Length > nine.Length, $"'{nine}' -> '{ten}' expected to grow by a digit");
    }

    [Fact]
    public void FixedWidth_PastTheBudget_FallsBackToAFixedWidthExponentForm()
    {
        // The widest-range quantities (impedance parts, dBm) are sized to need the exponent fallback
        // out at 1e6 — still a fixed decimal-place count, no padding.
        var wideQuantities = new (int Decimals, int Budget)[]
        {
            (HarmonicaReadoutFormatting.ComplexPartDecimals, HarmonicaReadoutFormatting.ComplexPartBudget),
            (HarmonicaReadoutFormatting.DbmDecimals,         HarmonicaReadoutFormatting.DbmBudget),
        };

        foreach (var (decimals, budget) in wideQuantities)
        {
            string s = HarmonicaReadoutFormatting.FixedWidth(1e6, decimals, budget);
            Assert.Contains('e', s);
            output.WriteLine($"FixedWidth(1000000, {decimals}, {budget}) = '{s}'");
        }

        // A quantity whose value stays within its own budget must NOT switch to exponent form.
        string watt = HarmonicaReadoutFormatting.FixedWidth(
            1e5, HarmonicaReadoutFormatting.WattDecimals, HarmonicaReadoutFormatting.WattBudget);
        Assert.DoesNotContain('e', watt);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(50)]
    [InlineData(5000)]
    [InlineData(-5000)]
    public void FormatZ_NoGapBetweenRealAndImaginary_AcrossZsWideDynamicRange(double re)
    {
        // R-hui-2 — the owner's own complaint: "x+j     y" (a padded gap between the real and
        // imaginary parts). FormatZ must never produce a space immediately after "+j"/"-j".
        string s = HarmonicaReadoutFormatting.FormatZ(new Complex(re, 0.1), ReadoutFormat.RealImaginary);
        int j = s.IndexOf('j');
        Assert.True(j >= 0 && j + 1 < s.Length && s[j + 1] != ' ', $"'{s}' has a gap after 'j'");
    }

    // ══ R-hui-4 — value/unit split for column-aligned rendering (replaces ReservedValueChars) ══════

    [Theory]
    [InlineData("-3.50 dBm", "-3.50", "dBm")]
    [InlineData("12.340 dB", "12.340", "dB")]
    [InlineData("45.6 %", "45.6", "%")]
    [InlineData("1.234 W", "1.234", "W")]
    [InlineData("158.806+j0.000 Ω", "158.806+j0.000", "Ω")]
    public void SplitUnit_RecognizesEveryKnownSuffix(string formatted, string expectedValue, string expectedUnit)
    {
        var (value, unit) = HarmonicaReadoutFormatting.SplitUnit(formatted);
        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedUnit, unit);
    }

    [Theory]
    [InlineData("no optimum")]      // a plain status string with a space — must NOT be misparsed
    [InlineData("not located")]
    [InlineData("0.500+j0.100")]    // a Γ row — no unit at all
    [InlineData("45.0°")]           // degrees attach with no space, by convention — stays one token
    [InlineData("—")]
    public void SplitUnit_LeavesAnythingWithNoKnownSuffixAlone(string formatted)
    {
        var (value, unit) = HarmonicaReadoutFormatting.SplitUnit(formatted);
        Assert.Equal(formatted, value);
        Assert.Equal("", unit);
    }

    // ══ R7C §1.1/§1.3 — the UNIT column is GONE; the unit rides in the LABEL, and a row's reserved
    // VALUE width is now a worst-case STRING (measured against the live typeface by ReadoutStripView),
    // not a character-count budget for a now-nonexistent unit column.

    [Fact]
    public void WorstCaseValueTexts_ReserveAtLeastAsManyDigitsAsARealFormattedValueNeeds()
    {
        // Every quantity's worst-case literal must be at least as long (digit-for-digit) as what a
        // real, in-range formatted value's own VALUE half (unit stripped) actually needs, or the
        // pinned reserved width would clip a legitimate value rather than merely leave slack.
        var pout = new HarmonicaReadout("Pout", HarmonicaReadoutFormatting.FormatDbm(-3.5), "", ReadoutColumn.OperatingPoint, Unit: "dBm");
        var eff  = new HarmonicaReadout("Eff",  HarmonicaReadoutFormatting.FormatPercent(45.6), "", ReadoutColumn.OperatingPoint, Unit: "%");
        var gain = new HarmonicaReadout("Gain", HarmonicaReadoutFormatting.FormatDb(12.3), "", ReadoutColumn.OperatingPoint, Unit: "dB");
        var pdc  = new HarmonicaReadout("Pdc",  HarmonicaReadoutFormatting.FormatWatt(1.234), "", ReadoutColumn.OperatingPoint, Unit: "W");
        var amPm = new HarmonicaReadout("AM/PM", HarmonicaReadoutFormatting.FormatDegrees(45.0), "", ReadoutColumn.OperatingPoint);
        var zRow = new HarmonicaReadout("ZL1", HarmonicaReadoutFormatting.FormatZ(new Complex(50, 10), ReadoutFormat.RealImaginary),
            "", ReadoutColumn.Load, IsComplex: true, Editable: true, RawValue: new Complex(50, 10), Unit: "Ω");

        foreach (var item in new[] { pout, eff, gain, pdc, amPm })
        {
            var (value, _) = HarmonicaReadoutFormatting.SplitUnit(item.Value);
            string worstCase = Assert.Single(HarmonicaReadoutFormatting.WorstCaseValueTexts(item));
            Assert.True(value.Length <= worstCase.Length,
                $"{item.Label}: '{value}' ({value.Length} chars) exceeds worst case '{worstCase}' ({worstCase.Length} chars)");
        }

        var (zValue, zUnit) = HarmonicaReadoutFormatting.SplitUnit(zRow.Value);
        Assert.Equal("Ω", zUnit);   // Format* functions still append the unit — SplitUnit still strips it
        var zWorstCases = HarmonicaReadoutFormatting.WorstCaseValueTexts(zRow);
        Assert.Equal(2, zWorstCases.Count);   // a complex row's format can flip live: rect AND polar
        Assert.Contains(zWorstCases, wc => zValue.Length <= wc.Length);

        // The intrinsic VDS/IDS chunks carry no per-row unit (it is stated once, in the header) but
        // are still complex rows whose format can flip, so they still get both candidates.
        var intrinsicRow = new HarmonicaReadout("1f0", "", "", ReadoutColumn.IntrinsicVds, IsComplex: true, Band: 1);
        Assert.Equal(2, HarmonicaReadoutFormatting.WorstCaseValueTexts(intrinsicRow).Count);
    }

    [Theory]
    [InlineData("Pout", "dBm", "Pout (dBm):")]
    [InlineData("Vgs", "", "Vgs:")]
    [InlineData("γ", "", "γ:")]
    public void LabelWithUnit_MergesTheUnitIntoTheLabelCell(string label, string unit, string expected)
    {
        // R7C §1.1 — "Merge the units to be with the metric name." Source-scanned, same reason every
        // other ReadoutStripView layout fact in this suite is (Ui.Tests cannot instantiate a live
        // control), but exercised via reflection here since LabelWithUnit is a pure function.
        var type = typeof(HarmonicaReadout).Assembly.GetType("CircuitRF.Ui.Views.Harmonica.ReadoutStripView");
        Assert.NotNull(type);
        var method = type!.GetMethod("LabelWithUnit", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, (string)method!.Invoke(null, [label, unit])!);
    }

    [Fact]
    public void LabelWithUnit_StripsAPreExistingTrailingColon_RatherThanDoublingIt()
    {
        // Two of HarmonicaInputs.Build's own labels ("Freq:", "HB Order:") already bake in a colon
        // for reasons unrelated to this render-time convention — LabelWithUnit must not produce
        // "Freq: (GHz):".
        var type = typeof(HarmonicaReadout).Assembly.GetType("CircuitRF.Ui.Views.Harmonica.ReadoutStripView");
        var method = type!.GetMethod("LabelWithUnit", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Equal("Freq (GHz):", (string)method!.Invoke(null, ["Freq:", "GHz"])!);
        Assert.Equal("HB Order:", (string)method!.Invoke(null, ["HB Order:", ""])!);
    }

    // ══ §5 — per-chunk Copy, source-scanned (Ui.Tests cannot instantiate a live control) ═════════

    [Fact]
    public void ReadoutStripView_AttachesOneCopyMenuPerChunk_ToAllSevenHosts()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");
        Assert.Contains("AttachChunkCopyMenu(host)", src, StringComparison.Ordinal);

        // R-hui-1 — Source and Load merged into one TerminationsColumn chunk.
        foreach (string chunk in new[]
                 { "SettingsColumn", "OperatingPointColumn", "TerminationsColumn",
                   "MxpColumn", "MxeColumn", "IntrinsicVdsColumn", "IntrinsicIdsColumn" })
            Assert.Contains(chunk, src, StringComparison.Ordinal);

        Assert.Contains("private void AttachChunkCopyMenu(StackPanel host)", src, StringComparison.Ordinal);
        Assert.Contains("HarmonicaClipboard.RowsText(rows)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyReadoutsAndPerChunkCopy_ShareTheSameFormatter()
    {
        // The brief's own instruction: "factor its inner loop out so the per-chunk copy and the
        // existing Edit > Copy Readouts share one formatter." Pinned as a source fact rather than by
        // constructing a live clipboard (Ui.Tests has no headless Avalonia Application/clipboard).
        string clipboardSrc = ReadSource("src", "Ui", "Harmonica", "HarmonicaClipboard.cs");
        Assert.Contains("public static string RowsText(", clipboardSrc, StringComparison.Ordinal);

        string viewSrc = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");
        Assert.Contains("HarmonicaClipboard.RowsText(rows.Select(r => (r.Label, r.Value)))",
                        viewSrc, StringComparison.Ordinal);
    }

    // ══ §1 — the switch that routes a readout to its column is now exhaustive ═══════════════════

    [Fact]
    public void SetItemsSwitch_HandlesEveryReadoutColumn_ExplicitlyRatherThanADefaultCase()
    {
        // R6C §2's own instruction: "SetItems's switch currently uses default: for Mxe, which
        // silently swallows any new enum member into the MXE column. Make that switch exhaustive
        // before adding anything to the enum." — pinned so a NINTH column added later cannot silently
        // fall through to whichever case happens to be last.
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("public void SetItems(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>One General-column row", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.DoesNotContain("default:", body, StringComparison.Ordinal);
        foreach (string c in new[]
                 { "ReadoutColumn.OperatingPoint", "ReadoutColumn.Source", "ReadoutColumn.Load",
                   "ReadoutColumn.Mxp", "ReadoutColumn.Mxe", "ReadoutColumn.IntrinsicVds", "ReadoutColumn.IntrinsicIds" })
            Assert.Contains($"case {c}:", body, StringComparison.Ordinal);
    }
}
