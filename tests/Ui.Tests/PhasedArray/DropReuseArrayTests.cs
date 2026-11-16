// brief-pch-10 R-pch10-4 — reuse across drops when the transmit end is a steered array whose beam the channel picks per drop: the
// transmit side is solved once per distinct beam, cached by its weights, and every drop equals the joint solve's. The array turns 15°
// every third drop (TxAz), so its best beam moves while its circuit does not: drops 1-2, 3-5, 6-8 and 9 each share a beam.

using System.Runtime.CompilerServices;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

public sealed class DropReuseArrayTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-pch10-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private string Bench => $"""
        drop = 1
        f0 = 5.9 GHz
        t1 = 0 deg
        p1 = 0 deg
        MOD_Source:S1 f1 0 Fc=5.9e9 Pavg=0 Z=50 Ohm Record=20e-6 Carrier[1]="CW;Off=0;P=0"
        PhasedArray:PA1 f1 Record="{Record("e1")}" dx=25.86 mm dy=25.86 mm Mode=feed Rows=8 Cols=8 Phase=steer(t1,p1,f0)
        PropagationChannel:CH1 rx Tx=PA1 Distance=100 TxHeight=1.5 RxHeight=1.5 f0=5.9 GHz TxAz=15*floor(drop/3) Seed=7 Drop=drop
        L:L1 rx m L=1e-9
        C:C1 m b C=0.73e-12
        IProbe:Irx b c
        R:RL c 0 R=50 Ohm
        analysis ENV1 type=env MaxHarm=1 Segments=1 Warmup=0.5e-6 Stop=3e-6
        analysis MC type=parametric_sweep Var=drop Start=1 Stop=9 Step=1 Inner=ENV1
        """;

    private DataSet Run(bool reuse, out (int Solves, int Reused, int Beams) counters)
    {
        var circuit = PreparedCircuit.FromText(Bench, _dir, _dir);
        Assert.True(circuit.ReadError is null, circuit.ReadError);
        var ds = ParametricSweepEngine.Run(circuit.Tb!.Analyses.OfType<ParametricSweepAnalysis>().Single(), circuit.Lib!, circuit.Tb!,
                                           new AnalysisSettings { ChannelDropReuse = reuse }, _dir);
        counters = EnvelopeDropReuse.LastCounters;
        return ds;
    }

    [Fact]
    public void ASteeredTransmitArray_IsSolvedOncePerDistinctBeam_AndEveryDropEqualsTheJointSolve()
    {
        var with = Run(true, out var on);
        var without = Run(false, out _);
        output.WriteLine($"9 drops: transmit side solved {on.Solves} time(s), {on.Beams} distinct beam(s), {on.Reused} drop(s) replayed");
        Assert.Equal(9, on.Solves + on.Reused);
        Assert.True(on.Solves >= 2, "the beam never moved");
        Assert.Equal(on.Solves, on.Beams);              // one transmit solve per distinct beam
        Assert.True(on.Reused > 0, "no drop shared a beam");

        double worst = 0;
        string worstName = "";
        foreach (var name in without.Cubes.Keys.Where(n => !n.StartsWith("__", StringComparison.Ordinal)))
        {
            var a = without[name]; var b = with[name];
            if (a.DataKind != DataKind.Complex && a.DataKind != DataKind.Real) continue;
            double peak = 0, diff = 0;
            if (a.DataKind == DataKind.Complex)
            {
                var x = a.ComplexValues; var y = b.ComplexValues;
                for (int i = 0; i < x.Length; i++) { if (double.IsNaN(x[i].Real)) continue; peak = Math.Max(peak, x[i].Magnitude); diff = Math.Max(diff, (x[i] - y[i]).Magnitude); }
            }
            else
            {
                var x = a.RealValues; var y = b.RealValues;
                for (int i = 0; i < x.Length; i++)
                {
                    if (double.IsNaN(x[i]) && double.IsNaN(y[i]) || double.IsInfinity(x[i]) && x[i] == y[i]) continue;
                    peak = Math.Max(peak, Math.Abs(x[i])); diff = Math.Max(diff, Math.Abs(x[i] - y[i]));
                }
            }
            double rel = peak > 0 ? diff / peak : diff;
            if (rel > worst) { worst = rel; worstName = name; }
        }
        output.WriteLine($"worst relative difference {worst:G3} ('{worstName}')");
        Assert.True(worst <= 1e-12, $"'{worstName}': {worst:G3}");
    }
}
