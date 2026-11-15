using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using RfCore.Propagation;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// brief-pch-6 R-pch6-2 — a pin end read from an EM result's far field, through the seam src/Design fills, here filled by the
/// test with a pattern polarized along its own +y (up, unrolled) with a realized gain of 4, 6 dBi, at boresight, recorded at 2 and 3 GHz with S11 = 0.1.
/// Two such ends facing each other co-polar give Friis times 4; rolling the receive end 90° about its boresight crosses the
/// polarizations and the coupling vanishes; S11 is the record's; a frequency the record lacks takes its nearest and says so.
/// Core.Tests loads no src/Design, so nothing else fills the seam while this runs.
/// </summary>
public sealed class PropagationChannelPatternTests
{
    private static ChannelPattern Fake(string path, int port, double hz)
    {
        double f = Math.Abs(hz - 2e9) <= Math.Abs(hz - 3e9) ? 2e9 : 3e9;
        return new ChannelPattern
        {
            FrequencyHz = f, Record = Path.GetFileName(path), S11 = new Complex(0.1, 0), Z0 = 50,
            // polarized along the pattern's own +y, which is defined at the pole: Eθ = 2·cos θ·sin φ, Eφ = 2·cos φ
            Field = (th, ph) => (2 * Math.Cos(th) * Math.Sin(ph), 2 * Math.Cos(ph)),
        };
    }

    private static PropagationChannelModel Channel(double rxRollDeg)
    {
        var p = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Distance"] = new Value(100.0),
            ["TxPattern"] = new Value("rec.cemr#1"),
            ["RxPattern"] = new Value("rec.cemr#1"),
            ["RxRoll"] = new Value(rxRollDeg),
        };
        return new PropagationChannelModel(p, 25);
    }

    [Fact]
    public void PatternEnds_GiveTheirGainPolarizationAndImpedance()
    {
        var before = PropagationChannelModel.PatternSource;
        PropagationChannelModel.PatternSource = Fake;
        try
        {
            var co = Channel(0);
            double friis = PropagationMath.C / 2e9 / (4 * Math.PI * 100);
            Assert.Equal(4 * friis, co.H(2e9).Magnitude, 1e-12 * friis);
            Assert.Empty(co.DrainWarnings());
            Assert.Equal(new Complex(0.1, 0), co.End(0, 2e9).S);
            Assert.Equal(new Complex(0.1, 0), co.End(1, 2e9).S);

            Assert.True(Channel(90).H(2e9).Magnitude < 1e-12 * friis);    // crossed polarizations

            _ = co.H(2.4e9);                                              // the record has 2 and 3 GHz: 2 is used, and said
            Assert.Contains(co.DrainWarnings(), w => w.Message.Contains("its nearest, 2 GHz, is used"));
        }
        finally { PropagationChannelModel.PatternSource = before; }
    }
}
