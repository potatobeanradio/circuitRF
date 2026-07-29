using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Backward compatibility (TLIN's A=0 default is byte-identical to before) and the new
/// shared StampUniformLine helper (brief-L5a-pcell-contract-and-microstrip.md R-pc-11/R3).</summary>
public class TLineModelLossTests
{
    private static ElaboratedComponent MakeEc(ComponentModel model, int[] nodes)
        => new("TLIN", "TL1", nodes, new Dictionary<string, Value>(), model);

    [Fact]
    public void DefaultLoss_ZeroDb_MatchesOriginalLosslessStamp()
    {
        var model = new TLineModel(50.0, Math.PI / 2.0, 1e9, "TL1"); // A defaults to 0
        var mna = new CapturingMnaContext();
        var ec = MakeEc(model, [1, 2]);
        model.Stamp(mna, ec, 2 * Math.PI * 1e9);

        // Lossless θ=π/2 at f=F: Y11 = -j*cot(π/2)/Z0 = 0; Y12 = j/(Z0*sin(π/2)) = j/50.
        Assert.Equal(0.0, mna.Entries[(1, 1)].Real, 1e-9);
        Assert.Equal(0.0, mna.Entries[(1, 1)].Imaginary, 1e-6);
        Assert.Equal(1.0 / 50.0, mna.Entries[(1, 2)].Imaginary, 1e-9);
    }

    [Fact]
    public void PositiveAttenuation_ProducesRealPartInY11()
    {
        var lossy = new TLineModel(50.0, Math.PI / 2.0, 1e9, "TL1", attenuationDb: 3.0);
        var mna = new CapturingMnaContext();
        var ec = MakeEc(lossy, [1, 2]);
        lossy.Stamp(mna, ec, 2 * Math.PI * 1e9);

        // A lossy line's Y11 must carry a nonzero real part (dissipation) — 0 for the lossless case.
        Assert.True(Math.Abs(mna.Entries[(1, 1)].Real) > 1e-9);
    }

    [Fact]
    public void StampUniformLine_ZeroLoss_ReducesExactlyToLosslessCotCsc()
    {
        double z0 = 75.0, theta = 1.234;
        var mna1 = new CapturingMnaContext();
        TLineModel.StampUniformLine(mna1, 1, 2, new Complex(z0, 0), new Complex(0.0, theta));

        double expectedY11Imag = -Math.Cos(theta) / (z0 * Math.Sin(theta));
        double expectedY12Imag = 1.0 / (z0 * Math.Sin(theta));

        Assert.Equal(expectedY11Imag, mna1.Entries[(1, 1)].Imaginary, 6);
        Assert.Equal(expectedY12Imag, mna1.Entries[(1, 2)].Imaginary, 6);
        Assert.Equal(0.0, mna1.Entries[(1, 1)].Real, 9);
    }

    [Fact]
    public void StampUniformLine_ResonanceGuard_NeverProducesNaNOrInfinity()
    {
        var mna = new CapturingMnaContext();
        // theta = pi exactly -> sin(theta) = 0 in the lossless case; the shared helper must clamp.
        TLineModel.StampUniformLine(mna, 1, 2, new Complex(50, 0), new Complex(0.0, Math.PI));
        foreach (var v in mna.Entries.Values)
        {
            Assert.False(double.IsNaN(v.Real) || double.IsNaN(v.Imaginary));
            Assert.False(double.IsInfinity(v.Real) || double.IsInfinity(v.Imaginary));
        }
    }

    [Fact]
    public void Factory_TLIN_ParsesOptionalADbParameter()
    {
        var parms = new Dictionary<string, Value>
        {
            ["Z"] = new Value(50.0),
            ["E"] = new Value(Math.PI / 2.0),
            ["F"] = new Value(1e9),
            ["A"] = new Value(6.0),
        };
        var model = ComponentModelFactory.TryCreate("TLIN", parms);
        Assert.NotNull(model);
        Assert.IsType<TLineModel>(model);
    }

    [Fact]
    public void Factory_TLIN_NoAParameter_StillConstructs()
    {
        var parms = new Dictionary<string, Value>
        {
            ["Z"] = new Value(50.0),
            ["E"] = new Value(Math.PI / 2.0),
            ["F"] = new Value(1e9),
        };
        var model = ComponentModelFactory.TryCreate("TLIN", parms);
        Assert.NotNull(model);
    }
}
