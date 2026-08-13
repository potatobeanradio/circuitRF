// ================================================================
//  HarmonicaReadoutFormatRepaintTests.cs — brief-harmonicarf-r2b R-h9r2-25
//
//  "The MenuFlyout used to set the format of the termination readout does not change the rendering of
//  the text to the new format that is selected by the user." Root-caused: HarmonicaReadout.Value is a
//  string baked in at SOLVE time, in whatever format was current then; OnReadoutFormatChanged wrote the
//  NEW format and called Refresh(), but Refresh() re-renders the SAME cached frame, so an idle
//  document's format change had nothing to make it repaint. Fixed by carrying RawValue (the unformatted
//  Complex) alongside Value and formatting it at RENDER time — pinned here via reflection into
//  ReadoutStripView's own private, pure DisplayValue helper (no live control/window needed).
// ================================================================

using System;
using System.Numerics;
using System.Reflection;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Views.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaReadoutFormatRepaintTests
{
    private static string InvokeDisplayValue(HarmonicaReadout item, Func<string, ReadoutFormat> formatFor)
    {
        var method = typeof(ReadoutStripView).GetMethod("DisplayValue",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [item, formatFor])!;
    }

    [Fact]
    public void ChangingFormat_RepaintsTheRow_WithNoReSolve_FromTheSameRawValue()
    {
        // A row exactly as HarmonicaSolver.BuildReadouts builds an editable Z row: Value baked at
        // "solve time" in real/imaginary, RawValue carrying the true unformatted impedance.
        var z = new Complex(80, 10);
        var item = new HarmonicaReadout("ZL1", HarmonicaReadoutFormatting.FormatZ(z, ReadoutFormat.RealImaginary),
            "tooltip", ReadoutColumn.Load, IsComplex: true, Editable: true,
            Side: TerminationSideKind.Load, Band: 1, IsGamma: false, RawValue: z);

        string beforeFormatChange = InvokeDisplayValue(item, _ => ReadoutFormat.RealImaginary);
        Assert.Equal("80+j10 Ω", beforeFormatChange);

        // The user picks Magnitude/Angle from the right-click menu — the SAME HarmonicaReadout object
        // (no re-solve happened), only the format resolver's answer changed.
        string afterFormatChange = InvokeDisplayValue(item, _ => ReadoutFormat.MagnitudeAngle);

        Assert.NotEqual(beforeFormatChange, afterFormatChange);
        Assert.Equal(HarmonicaReadoutFormatting.FormatZ(z, ReadoutFormat.MagnitudeAngle), afterFormatChange);
    }

    [Fact]
    public void GammaRow_AlsoRepaints_FromItsOwnRawValue()
    {
        var g = new Complex(0.35, -0.20);
        var item = new HarmonicaReadout("ΓL1", HarmonicaReadoutFormatting.FormatGamma(g, ReadoutFormat.RealImaginary),
            "tooltip", ReadoutColumn.Load, IsComplex: true, Editable: true,
            Side: TerminationSideKind.Load, Band: 1, IsGamma: true, RawValue: g);

        string before = InvokeDisplayValue(item, _ => ReadoutFormat.RealImaginary);
        string after  = InvokeDisplayValue(item, _ => ReadoutFormat.MagnitudeAngle);

        Assert.NotEqual(before, after);
        Assert.Equal(HarmonicaReadoutFormatting.FormatGamma(g, ReadoutFormat.MagnitudeAngle), after);
    }

    [Fact]
    public void ARowWithNoRawValue_FallsBackToItsPreFormattedValue()
    {
        // Headers, "no optimum", and every scalar figure (Pout, Gain, DE, PAE, …) carry no RawValue —
        // DisplayValue must fall back to Value rather than throwing or blanking the row.
        var scalar = new HarmonicaReadout("Pout", "12.3 dBm", "tooltip", ReadoutColumn.Mxp);
        Assert.Equal("12.3 dBm", InvokeDisplayValue(scalar, _ => ReadoutFormat.MagnitudeAngle));

        var header = new HarmonicaReadout("MXP 1f0 Load", "", "", ReadoutColumn.Mxp);
        Assert.Equal("", InvokeDisplayValue(header, _ => ReadoutFormat.MagnitudeAngle));
    }

    [Fact]
    public void ZinRow_MXPMXE_AlsoCarriesRawValue_AndRepaintsOnFormatChange()
    {
        // R-h9r2-25 applies uniformly to every IsComplex row, not only editable ones — MXP/MXE's Zin
        // has a right-click format flyout (BuildFormatMenu checks FormatKey, which Zin has) but is
        // never editable.
        var zin = new Complex(45, -12);
        var item = new HarmonicaReadout("Zin", HarmonicaReadoutFormatting.FormatZ(zin, ReadoutFormat.RealImaginary),
            "tooltip", ReadoutColumn.Mxp, IsComplex: true, RawValue: zin);

        Assert.False(item.Editable);
        Assert.NotNull(item.FormatKey);

        string before = InvokeDisplayValue(item, _ => ReadoutFormat.RealImaginary);
        string after  = InvokeDisplayValue(item, _ => ReadoutFormat.MagnitudeAngle);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void BuildReadouts_PopulatesRawValue_OnEveryComplexRow()
    {
        // Integration check through the real solver: every IsComplex row in a solved frame carries a
        // RawValue — the thing R-h9r2-25's whole fix depends on.
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        foreach (var r in vm.Frame.Readouts)
        {
            if (!r.IsComplex) continue;
            Assert.True(r.RawValue.HasValue, $"row '{r.Label}' (column {r.Column}) is IsComplex but carries no RawValue");
        }
    }
}
