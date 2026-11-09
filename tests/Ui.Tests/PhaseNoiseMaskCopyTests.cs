// Brief NZ-10 — "Copy as Phase-Noise Mask": an oscillator's PN trace as a source's PhaseNoise= key, and no such item on any
// other trace.

using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Signals;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PhaseNoiseMaskCopyTests
{
    private static Trace Bound(string cube, string xAxis, CubeTransform transform = CubeTransform.None)
    {
        var t = new Trace(new SNP(new[] { 1e9 }, 1), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cube,
            Slice = new[] { new AxisSlice(xAxis, AxisRole.KeepAsX, 0) },
            Transform = transform,
        };
        t.SetCubeData([100, 1e3, 1e4, 1e5], complexValues: null, [-71.19, -91.19, -111.19, -131.2], xAxis, "Hz",
                      PlotType.Rect, FreqUnit.GHz);
        return t;
    }

    [Fact]
    public void APnTrace_CopiesAsAMaskThatReadsBack_AndNoOtherTraceOffersIt()
    {
        string? text = PlotControl.PhaseNoiseMaskText(Bound("OSC1.PN", "offset"));
        Assert.Equal("PhaseNoise=\"100Hz:-71.19, 1kHz:-91.19, 10kHz:-111.19, 100kHz:-131.20\"", text);
        var mask = PhaseNoiseMask.Parse(text!["PhaseNoise=".Length..]);
        Assert.Equal(-111.19, mask.At(1e4), 9);

        Assert.Null(PlotControl.PhaseNoiseMaskText(Bound("OSC1.AN", "offset")));
        Assert.Null(PlotControl.PhaseNoiseMaskText(Bound("OSC1.PN", "offset", CubeTransform.Mag)));
        Assert.Null(PlotControl.PhaseNoiseMaskText(Bound("SP1.PN", "freq")));
    }
}
