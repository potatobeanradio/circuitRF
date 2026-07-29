using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Gate 11 (brief-L5a-pcell-contract-and-microstrip.md, R-pc-11): "a lossless MLIN's
/// s-parameters match an ideal TLIN of the same computed Z0 and electrical length" — proven here by
/// comparing the raw MNA admittance stamps directly (both models share the exact same
/// TLineModel.StampUniformLine call, so matching Z0/gamma implies matching stamps implies matching
/// S-parameters).</summary>
public class MicrostripLineModelTests
{
    private static ElaboratedComponent MakeEc(ComponentModel model, string type, int[] nodes)
        => new(type, "X1", nodes, new Dictionary<string, Value>(), model);

    [Fact]
    public void LosslessMlin_MatchesIdealTlinOfSameComputedZ0AndElectricalLength()
    {
        double w = 2.9e-3, l = 10e-3, h = 1.6e-3, t = 35e-6, er = 4.4;
        double freqHz = 2e9;

        // "Lossless" per the gate's own wording: extremely high conductivity, zero loss tangent.
        var mlin = new MicrostripLineModel(w, l, h, t, er, sigmaSPerM: 1e30, tanD: 0.0, "MLIN:X1");
        var mnaMlin = new CapturingMnaContext();
        mlin.Stamp(mnaMlin, MakeEc(mlin, "MLIN", [1, 2]), 2 * Math.PI * freqHz);

        // Independently compute what MLIN's own Z0(f)/eeff(f) are, and build an ideal TLIN with
        // EXACTLY that Z0 and electrical length at this one frequency.
        var reporter = new MicrostripValidityReporter("check");
        var (z0Static, eeff0) = HammerstadJensen.Compute(w, h, t, er, reporter);
        var (z0, eeff) = KirschningJansen.Compute(freqHz, w / h, er, h, z0Static, eeff0, reporter);
        double beta = 2 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(eeff);
        double thetaRad = beta * l;

        var tlin = new TLineModel(z0, thetaRad, freqHz, "TLIN:X2");
        var mnaTlin = new CapturingMnaContext();
        tlin.Stamp(mnaTlin, MakeEc(tlin, "TLIN", [1, 2]), 2 * Math.PI * freqHz);

        foreach (var key in mnaTlin.Entries.Keys)
        {
            Assert.True(mnaMlin.Entries.ContainsKey(key));
            var a = mnaMlin.Entries[key];
            var b = mnaTlin.Entries[key];
            Assert.Equal(b.Real, a.Real, 3);
            Assert.Equal(b.Imaginary, a.Imaginary, 3);
        }
    }

    [Fact]
    public void Stamp_WithRealisticLoss_ProducesNonzeroDissipation()
    {
        var model = new MicrostripLineModel(2.9e-3, 10e-3, 1.6e-3, 35e-6, 4.4,
            sigmaSPerM: 5.8e7, tanD: 0.02, "MLIN:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MLIN", [1, 2]), 2 * Math.PI * 2e9);

        Assert.True(Math.Abs(mna.Entries[(1, 1)].Real) > 0);
    }

    [Fact]
    public void Stamp_AtDcOmegaZero_DoesNotThrow_NoNaN()
    {
        var model = new MicrostripLineModel(2.9e-3, 10e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MLIN:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MLIN", [1, 2]), 0.0);
        foreach (var v in mna.Entries.Values)
        {
            Assert.False(double.IsNaN(v.Real) || double.IsNaN(v.Imaginary));
        }
    }

    [Fact]
    public void Factory_CreatesMicrostripLineModel_WithDefaultsWhenSubstrateOmitted()
    {
        var parms = new Dictionary<string, Value>
        {
            ["W"] = new Value(2.9e-3),
            ["L"] = new Value(10e-3),
        };
        var model = ComponentModelFactory.TryCreate("MLIN", parms);
        Assert.NotNull(model);
        Assert.IsType<MicrostripLineModel>(model);
    }

    [Fact]
    public void Factory_MLIN_ExplicitSubstrateOverridesUsed()
    {
        var parms = new Dictionary<string, Value>
        {
            ["W"] = new Value(600e-6),
            ["L"] = new Value(5e-3),
            ["H"] = new Value(635e-6),
            ["T"] = new Value(0.0),
            ["Er"] = new Value(4.1),
            ["Sigma"] = new Value(5.8e7),
            ["TanD"] = new Value(0.0),
        };
        var model = ComponentModelFactory.TryCreate("MLIN", parms);
        Assert.NotNull(model);
        var mna = new CapturingMnaContext();
        model!.Stamp(mna, MakeEc(model, "MLIN", [1, 2]), 2 * Math.PI * 1e9);
        Assert.NotEmpty(mna.Entries);
    }
}
