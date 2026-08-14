// ================================================================
//  HarmonicaR6cStripTests.cs  —  brief-harmonicarf-r6c-readout-strip-layout-and-stability
//
//  §1  the 2 x 4 chunk grid (pinned in HarmonicaR3cStripTests.ColumnsGrid_PlacesTheEightChunks...).
//  §2  Intrinsic VDS / Intrinsic IDS — two new chunks, read from the already-published V_intr/I_intr
//      cubes, magnitude ∠ angle by default.
//  §3  Settings label renames (compr -> "Compression:", f0 -> "Freq:", K -> "Harmonic Order:").
//  §4  fixed-width formatting — every produced string for a given row TYPE has the same length, and
//      that length does not depend on the row's own value.
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

        Assert.Equal("Compression:", byKey[HarmonicaInputs.KeyCompression].Label);
        Assert.Equal("Freq:",        byKey[HarmonicaInputs.KeyFrequency].Label);
        Assert.Equal("Harmonic Order:", byKey[HarmonicaInputs.KeyHarmonicCount].Label);

        // Vds/Vgs/Z0 are untouched — only the three named labels moved.
        Assert.Equal("Vds", byKey[HarmonicaInputs.KeyVds].Label);
        Assert.Equal("Vgs", byKey[HarmonicaInputs.KeyVgs].Label);
    }

    // ══ §4.1/§4.3 — fixed-width formatting ═══════════════════════════════════════════════════════

    // 0.001 .. 5000 plus the degenerate 9.999/10.001 crossing — within EVERY current row budget's own
    // fixed-decimal branch (none of these need the exponent fallback), so this exercises the ordinary
    // "value moved, string length must not" case every row type actually sees in practice. A separate
    // test below exercises the exponent fallback itself, which only the widest-budget quantities
    // (impedance, watts) are sized to survive out to 1e6 — a narrow-budget quantity like an angle was
    // never meant to hold a million degrees, so it is not swept there.
    private static readonly double[] SweepMagnitudes =
        [0.001, 0.01, 0.1, 1, 9.999, 10.001, 50, 100, 1000, 5000];

    [Fact]
    public void FixedWidth_ProducesTheSameLength_AcrossASweptRangeOfMagnitudesAndSigns()
    {
        // Each (decimals, budget) pair here is one of the REAL per-quantity budgets §4.1 defines —
        // every one of them is sized to hold its own worst case (including the exponent fallback), so
        // sweeping a wide magnitude range must never change the produced length. An arbitrarily small
        // budget CAN legitimately be too narrow even for the exponent form (a documented edge case of
        // FixedWidth's own contract) — that is not this test's concern.
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
        {
            int? expectedLength = null;
            foreach (double mag in SweepMagnitudes)
            foreach (double value in new[] { mag, -mag })
            {
                string s = HarmonicaReadoutFormatting.FixedWidth(value, decimals, budget);
                expectedLength ??= s.Length;
                Assert.True(s.Length == expectedLength,
                    $"FixedWidth({value}, {decimals}, {budget}) = '{s}' ({s.Length} chars), " +
                    $"expected {expectedLength} chars (row's own budget: {budget})");
            }
        }
    }

    [Fact]
    public void FixedWidth_PastTheBudget_FallsBackToAFixedWidthExponentForm_UpTo1e6()
    {
        // The widest-range quantities (impedance parts, watts, dBm) are the ones the brief itself
        // names as needing the exponent fallback — swept out to 1e6, both signs, still constant length.
        var wideQuantities = new (int Decimals, int Budget)[]
        {
            (HarmonicaReadoutFormatting.ComplexPartDecimals, HarmonicaReadoutFormatting.ComplexPartBudget),
            (HarmonicaReadoutFormatting.WattDecimals,        HarmonicaReadoutFormatting.WattBudget),
            (HarmonicaReadoutFormatting.DbmDecimals,         HarmonicaReadoutFormatting.DbmBudget),
        };

        foreach (var (decimals, budget) in wideQuantities)
        {
            int? expectedLength = null;
            foreach (double value in new[] { 1e6, -1e6, 12345.678, -12345.678 })
            {
                string s = HarmonicaReadoutFormatting.FixedWidth(value, decimals, budget);
                expectedLength ??= s.Length;
                Assert.Equal(expectedLength, s.Length);
                output.WriteLine($"FixedWidth({value}, {decimals}, {budget}) = '{s}'");
            }
        }
    }

    [Fact]
    public void FixedWidth_TheDegenerateTrailingZeroCrossing_KeepsTheSameLength()
    {
        // The brief's own motivating case: 9.99 -> 10.01 must not lose or gain a character.
        string before = HarmonicaReadoutFormatting.FixedWidth(9.999, 2, 9);
        string after  = HarmonicaReadoutFormatting.FixedWidth(10.001, 2, 9);
        Assert.Equal(before.Length, after.Length);
        output.WriteLine($"'{before}' -> '{after}'");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(50)]
    [InlineData(5000)]
    [InlineData(-5000)]
    public void FormatZ_NeverChangesLength_AcrossZsWideDynamicRange(double re)
    {
        // §0's own motivating complaint — an impedance running 0.5 Ω .. 5000 Ω during a drag must not
        // change the rendered string's length (and so must not reflow the column it sits in).
        string baseline = HarmonicaReadoutFormatting.FormatZ(new Complex(0.5, 0.1), ReadoutFormat.RealImaginary);
        string swept     = HarmonicaReadoutFormatting.FormatZ(new Complex(re, 0.1), ReadoutFormat.RealImaginary);
        Assert.Equal(baseline.Length, swept.Length);
    }

    [Fact]
    public void ReservedValueChars_IsAFunctionOfTheRowKind_NeverOfItsValue()
    {
        // R6C §4.2 — the same row TYPE (a Load Z row) reserves the identical width whether its current
        // value is small or huge; only the LABEL/IsComplex/IsGamma shape may change it.
        var small = new HarmonicaReadout("ZL1", HarmonicaReadoutFormatting.FormatZ(new Complex(0.5, 0), ReadoutFormat.RealImaginary),
            "tip", ReadoutColumn.Load, IsComplex: true, Editable: true, RawValue: new Complex(0.5, 0));
        var big = small with
        {
            Value = HarmonicaReadoutFormatting.FormatZ(new Complex(5000, 0), ReadoutFormat.RealImaginary),
            RawValue = new Complex(5000, 0),
        };

        Assert.Equal(HarmonicaReadoutFormatting.ReservedValueChars(small),
                     HarmonicaReadoutFormatting.ReservedValueChars(big));

        // A bare Gamma row (no unit) reserves LESS than a Z row (" Ω") of the same complex shape.
        var gamma = small with { IsGamma = true };
        Assert.True(HarmonicaReadoutFormatting.ReservedValueChars(gamma) <
                    HarmonicaReadoutFormatting.ReservedValueChars(small));
    }

    // ══ §5 — per-chunk Copy, source-scanned (Ui.Tests cannot instantiate a live control) ═════════

    [Fact]
    public void ReadoutStripView_AttachesOneCopyMenuPerChunk_ToAllEightHosts()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");
        Assert.Contains("AttachChunkCopyMenu(host)", src, StringComparison.Ordinal);

        foreach (string chunk in new[]
                 { "SettingsColumn", "OperatingPointColumn", "SourceColumn", "LoadColumn",
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
