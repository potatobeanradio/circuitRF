// ================================================================
//  HarmonicaIdealBiasNetworkTests.cs — Round 10 (owner): "I want a clear 50 ohm"
//
//  The shipped default document's gate IS a plain 50 Ω resistor (I[1,0] = _v1/50), so Zin at the
//  source plane should be 50 Ω and nothing else. It used to read 49.9992 + j0.1989 Ω, which is
//  exactly 50 ‖ jωL for the 1 µH bias choke DefaultModel used to override in — 12.57 kΩ at 2 GHz,
//  small enough to look like noise and large enough to be read off the strip. The choke and the DC
//  block are now HarmonicaSettings' own ideal 1 H / 1 F.
// ================================================================

using System;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaIdealBiasNetworkTests(ITestOutputHelper output)
{
    [Fact]
    public void TheDefaultDocument_HasAnIdealBiasNetwork()
    {
        var s = HarmonicaViewModel.DefaultModel().Settings;
        Assert.Equal(HarmonicaNetlist.IdealChokeH, s.BiasChokeHenries);
        Assert.Equal(HarmonicaNetlist.IdealBlockF, s.DcBlockFarads);
    }

    [Fact]
    public void TheDefaultDocuments_SourceZin_IsAClean50Ohms()
    {
        var model = HarmonicaViewModel.DefaultModel();
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Load, 1, new Complex(80, 0));

        var ctx = HarmonicaContext.Create(model);
        var op  = ctx.Solve(terms, pavlDbm: -30);
        var zin = (Complex)HarmonicaDataSet.Build(ctx, op, terms)["Zin"]
                           [(int)TerminationSide.Source, 1];

        output.WriteLine($"Zin(source, f0) = {zin}");

        // 1e-6 relative is far tighter than the 1 µH choke's own signature (which was 1.6e-5 in the
        // real part and 4e-3 in the imaginary), so this fails outright if the override comes back.
        Assert.Equal(50.0, zin.Real, precision: 6);
        Assert.True(Math.Abs(zin.Imaginary) < 1e-5,
            $"the plane should be purely resistive; Im(Zin) = {zin.Imaginary:E3}");
    }
}
