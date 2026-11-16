using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief PCH-7's gates on the component (the generator's own statistics and the profiles are RfCore's FadingTests):
///   5  Stateless: drop 37 alone equals drop 37 inside a 100-drop parametric sweep, bit for bit; adding a tap to a file
///      leaves every other tap's fading unchanged.
///   6  Inter-carrier interference: an NR signal through flat Rayleigh fading, genie-equalized, has the EVM
///      testdata/propagation/ici_reference.py predicts for the drop's own gain within 10 % at three Dopplers.
/// </summary>
public sealed class PropagationChannelFadingTests(ITestOutputHelper output)
{
    private static string Inv(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void Gate5_DropAlone_EqualsTheSameDropInASweep_AndATapAddedMovesNoOther()
    {
        const string channel = "Distance=200 Multipath=tdl Profile=TDL-C DelaySpread=300e-9 fD=50 Seed=11 Drop=drop TimeOffset=0.0137";
        var (lib, tb) = new CnlReader().Read($"""
            drop = 1
            Port:P1 tx 0 Num=1 Z=50 Ohm
            Port:P2 rx 0 Num=2 Z=50 Ohm
            PropagationChannel:CH1 tx rx {channel}
            analysis SP1 type=sparam start=2 stop=2.02 npts=3 Unit=GHz
            analysis MC type=parametric_sweep Var=drop Start=1 Stop=100 Step=1 Inner=SP1
            """);
        var swept = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb)["S"];

        var (lib1, tb1) = new CnlReader().Read($"""
            Port:P1 tx 0 Num=1 Z=50 Ohm
            Port:P2 rx 0 Num=2 Z=50 Ohm
            PropagationChannel:CH1 tx rx {channel.Replace("Drop=drop", "Drop=37")}
            """);
        using var nl = new Elaborator(lib1).Elaborate(tb1);
        double[] hz = [2e9, 2.01e9, 2.02e9];
        var alone = SParameterEngine.Run(nl, hz)["S"];
        int per = alone.ComplexValues.Length;
        var inSweep = swept.ComplexValues.AsSpan(36 * per, per).ToArray();
        output.WriteLine($"drop 37: S21 alone {alone.ComplexValues[2]}, in the sweep {inSweep[2]}; drop 36 in the sweep {swept.ComplexValues[35 * per + 2]}");
        Assert.Equal(alone.ComplexValues, inSweep);
        Assert.NotEqual(swept.ComplexValues[35 * per + 2], inSweep[2]);

        // a tap added to a file: every other tap's fading is the same function of time
        string dir = Path.Combine(Path.GetTempPath(), "crf-pch7-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "two.csv"), "delay (ns), power (dB)\n0, 0\n100, -3\n");
            File.WriteAllText(Path.Combine(dir, "three.csv"), "delay (ns), power (dB)\n0, 0\n100, -3\n250, -6\n");
            PropagationChannelModel.Realization Real(string file)
            {
                var (l, t) = new CnlReader().Read($"""
                    PropagationChannel:CH1 tx rx Distance=200 Multipath=file File="{Path.Combine(dir, file)}" Fading=rayleigh fD=50 Seed=3 Drop=5
                    R:RL rx 0 R=50 Ohm
                    """);
                using var n = new Elaborator(l).Elaborate(t);
                return ((PropagationChannelModel)n.Components.Single(c => c.Model is PropagationChannelModel).Model).At(2e9);
            }
            var two = Real("two.csv");
            var three = Real("three.csv");
            for (int i = 0; i < 2; i++)
                foreach (double t in new[] { 0.0, 0.0123, 0.5 })
                    Assert.Equal(two.Fades[i].Gain(t), three.Fades[i].Gain(t));
            Assert.Contains(three.Notes, n => n.Contains("normalized to 0 dB"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static string Reference(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var p = Path.Combine(dir, "testdata", "propagation", name);
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Gate6_FastFading_IciEvm_MatchesTheIndependentPrediction()
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Reference("ici_reference.json")));
        var root = json.RootElement;
        long seed = root.GetProperty("seed").GetInt64();
        double dt = root.GetProperty("dt_s").GetDouble();
        Assert.Equal(RfCore.Propagation.FadingProcess.DefaultSinusoids, root.GetProperty("sinusoids").GetInt32());
        double last = 0;
        foreach (var dop in root.GetProperty("dopplers").EnumerateArray())
        {
            double fd = dop.GetProperty("fd_hz").GetDouble();
            var e = dop.GetProperty("error_power").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            // unshaped, so the cyclic prefix is an exact copy; the window is not a whole record, so no symbol wraps it
            string cnl = $"""
                MOD_Source:S1 s 0 Fc=2e9 Pavg=-20 Z=50 Ohm Record=1e-3 Shaping=off Carrier[1]="NR;BW=10MHz;SCS=30kHz;Mod=16QAM;Off=0;P=0;Seed=1"
                PropagationChannel:CH1 s rx Distance=1 TxGain=10 RxGain=10 Fading=rayleigh fD={Inv(fd)} Seed={seed} Drop=1
                IProbe:Irx rx c
                R:RL c 0 R=50 Ohm Noise=no
                analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=5e-6 Stop=0.95e-3 Equalizer=genie
                """;
            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
            bool used = false;
            ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
            var (r, skip, _) = EnvelopeRunner.Record(plan, Elab);
            var ds = EnvelopeDataSet.Build(r, plan, skip, s => output.WriteLine("note: " + s));
            double evm = ds["EVM_pct"].RealValues[0];

            // the symbols the meter read, and where each sat in channel time (the window's start plus its middle)
            int t0 = (int)Math.Round(skip / r.StepS);
            int b = Array.IndexOf(r.IProbeNames, "Irx");
            var y = Enumerable.Range(t0, r.Time.Length - t0).Select(t => r.IProbeVoltage[t][b][1]).ToArray();
            long start = (long)Math.Round(plan.Params.StartS / r.StepS) + t0;
            var rec = r.Sources[0].Signal.Carriers[0];
            var d = RfCore.Signals.SignalEvm.Demodulate(rec, r.Sources[0].Signal.RecordSeconds, y, 1 / r.StepS, start, 0, out var why);
            Assert.True(d is not null, why);
            double mean = d!.SymbolTimeS!.Select(ts => start * r.StepS + ts).Average(tc =>
            {
                double i = tc / dt; int j = (int)Math.Floor(i);
                return e[j] + (i - j) * (e[j + 1] - e[j]);
            });
            double predicted = 100 * Math.Sqrt(mean);
            output.WriteLine($"f_D {fd} Hz (f_D/SCS {fd / 30e3:G3}): EVM {evm:G5} %, predicted {predicted:G5} % over {d.Symbols} symbols " +
                             $"({evm / predicted - 1:P1})");
            Assert.True(Math.Abs(evm / predicted - 1) < 0.10, $"f_D {fd}: EVM {evm} % against {predicted} %");
            Assert.True(evm > last, $"EVM {evm} % at f_D {fd} Hz does not rise from {last} %");
            last = evm;
        }
    }
}
