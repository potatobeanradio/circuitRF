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

    // brief-artsch-2 R-as2-2: θ = 2π·f·L·√Eeff/c₀ and αl = (Ac·√(f/F) + Ad·f/F)·L/8.686, through the factory; brief-trn-4b
    // adds the losses' phase to θ.
    [Fact]
    public void PhysicalForm_WithAcAndAd_MatchesTheClosedForm()
    {
        const double z0 = 42.0, l = 0.037, eeff = 2.7, fRef = 2e9, ac = 3.5, ad = 1.25;
        var model = ComponentModelFactory.TryCreate("TLIN", new Dictionary<string, Value>
        {
            ["Z"] = new Value(z0), ["L"] = new Value(l), ["Eeff"] = new Value(eeff),
            ["F"] = new Value(fRef), ["Ac"] = new Value(ac), ["Ad"] = new Value(ad),
        })!;

        foreach (double f in new[] { 0.5e9, 2e9, 7.3e9 })
        {
            var mna = new CapturingMnaContext();
            model.Stamp(mna, MakeEc(model, [1, 2]), 2 * Math.PI * f);

            double acL = ac * Math.Sqrt(f / fRef) * l / (20 / Math.Log(10)), adL = ad * f / fRef * l / (20 / Math.Log(10));
            // brief-trn-4b: the losses' own phase — the conductor's equal to its √f loss, the dielectric's pinned at F
            double theta = 2 * Math.PI * f * l * Math.Sqrt(eeff) / 2.99792458e8 + acL - 2 / Math.PI * adL * Math.Log(f / fRef);
            double alphaL = acL + adL;
            var gl = new Complex(alphaL, theta);
            Complex y11 = Complex.Cosh(gl) / (z0 * Complex.Sinh(gl));
            Complex y12 = -1.0 / (z0 * Complex.Sinh(gl));

            Assert.True((mna.Entries[(1, 1)] - y11).Magnitude <= 1e-12 * y11.Magnitude, $"Y11 at {f:G3} Hz");
            Assert.True((mna.Entries[(1, 2)] - y12).Magnitude <= 1e-12 * y12.Magnitude, $"Y12 at {f:G3} Hz");
        }
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
