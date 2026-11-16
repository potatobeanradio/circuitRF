using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief PCH-10's gate 1: reuse across drops is exact. A 50-drop envelope sweep over a fading channel, a nonlinear PA on the transmit
/// side and a receive side with memory (a series LC into a load), run with the reuse and without: every cube of every drop equals the
/// joint solve's to 1e-12 of its own peak, and the transmit side was solved once against 50 times.
/// </summary>
public sealed class DropReuseTests(ITestOutputHelper output)
{
    private const string Bench = """
        drop = 1
        MOD_Source:S1 s 0 Fc=2e9 Pavg=-10 Z=50 Ohm Record=20e-6 Carrier[1]="CW;BW=1MHz;Off=0;P=0"
        Amp:PA s 0 a 0 Gain=20 IP3=20
        IProbe:Itx a tx
        PropagationChannel:CH1 tx rx Distance=40 TxGain=10 RxGain=6 Fading=rayleigh fD=300 Seed=5 Drop=drop
        L:L1 rx m L=2e-9
        C:C1 m b C=3.1e-12
        IProbe:Irx b c
        R:RL c 0 R=50 Ohm Noise=no
        analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=0.5e-6 Stop=4e-6
        analysis MC type=parametric_sweep Var=drop Start=1 Stop=50 Step=1 Inner=ENV1
        """;

    private static DataSet Run(bool reuse, out (int, int, int) counters)
    {
        var (lib, tb) = new CnlReader().Read(Bench);
        var ds = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb,
                                           new AnalysisSettings { ChannelDropReuse = reuse });
        counters = EnvelopeDropReuse.LastCounters;
        return ds;
    }

    [Fact]
    public void Gate1_ReuseIsExact_AndSolvesTheTransmitSideOnce()
    {
        var with = Run(true, out var on);
        var without = Run(false, out var off);
        output.WriteLine($"counters with reuse {on}, without {off}");
        Assert.Equal((1, 49, 0), on);
        Assert.Equal((0, 0, 0), off);

        int compared = 0;
        double worst = 0;
        string worstName = "";
        foreach (var name in without.Cubes.Keys)
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;     // wall clock, step counts: the run's own bookkeeping
            var a = without[name];
            Assert.True(with.Contains(name), $"'{name}' is missing with reuse");
            var b = with[name];
            Assert.Equal(a.Axes.Select(x => x.Length), b.Axes.Select(x => x.Length));
            double peak = 0, diff = 0;
            if (a.DataKind == DataKind.Complex)
            {
                var x = a.ComplexValues; var y = b.ComplexValues;          // each read copies: read once
                for (int i = 0; i < x.Length; i++)
                {
                    if (double.IsNaN(x[i].Real)) continue;
                    peak = Math.Max(peak, x[i].Magnitude);
                    diff = Math.Max(diff, (x[i] - y[i]).Magnitude);
                }
            }
            else
            {
                var x = a.RealValues; var y = b.RealValues;
                for (int i = 0; i < x.Length; i++)
                {
                    if (double.IsNaN(x[i]) && double.IsNaN(y[i])) continue;
                    if (double.IsInfinity(x[i]) && x[i] == y[i]) continue;
                    peak = Math.Max(peak, Math.Abs(x[i]));
                    diff = Math.Max(diff, Math.Abs(x[i] - y[i]));
                }
            }
            double rel = peak > 0 ? diff / peak : diff;
            if (rel > worst) { worst = rel; worstName = name; }
            compared++;
        }
        output.WriteLine($"{compared} cubes compared over 50 drops; the worst relative difference {worst:G3} ('{worstName}')");
        Assert.True(compared > 10);
        Assert.True(worst <= 1e-12, $"'{worstName}': {worst:G3}");
    }
}
