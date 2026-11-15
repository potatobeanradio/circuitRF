using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using RfCore.Propagation;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief PCH-6's gates for the Propagation Channel with pin ends, each against a closed form or another run:
///   1  Friis: S21 = λ/(4πd) in an S-parameter run to 1e-12, and HB's delivered power the closed form to 1e-9 dB.
///   2  Explicit equals joint: the receive side driven by the recorded transmit wave equals the joint run to 1e-12.
///   3  Doppler: a CW through one ray at ν = 1 kHz lands at +1 kHz, to one bin; two rays of distinct Doppler beat at
///      their difference.
///   4  Delay: a two-ray static channel's envelope response is H(f) to 1e-6 across the band, its second ray a non-integer
///      number of steps late (with the run's one-step latency taken out).
///   5  One way: changing the receive side changes nothing on the transmit side, bit for bit.
/// The cycle refusal (gate 6) is CheckChannelTests' in Ui.Tests, where check lives.
/// </summary>
public sealed class PropagationChannelTests(ITestOutputHelper output)
{
    private static string Dir => Path.Combine(Path.GetTempPath(), "crf-pch6-" + Guid.NewGuid().ToString("N")[..8]);

    private static (Library, TestBench) Read(string cnl) => new CnlReader().Read(cnl);

    [Fact]
    public void Gate1_Friis_SParametersAndHb()
    {
        const double d = 250;
        var (lib, tb) = Read($"""
            Port:P1 tx 0 Num=1 Z=50 Ohm
            Port:P2 rx 0 Num=2 Z=50 Ohm
            PropagationChannel:CH1 tx rx Distance={d}
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        double[] hz = [0.9e9, 2.4e9, 28e9];
        var s = SParameterEngine.Run(nl, hz)["S"];
        for (int f = 0; f < hz.Length; f++)
        {
            double friis = PropagationMath.C / hz[f] / (4 * Math.PI * d);
            var s21 = s.ComplexValues[(f * 2 + 1) * 2 + 0];
            output.WriteLine($"{hz[f] / 1e9} GHz: S21 {s21}, Friis {friis:G17}");
            Assert.True((s21 - friis).Magnitude < 1e-12 * friis, $"S21 {s21} against {friis}");
            Assert.Equal(0, s.ComplexValues[(f * 2 + 0) * 2 + 1].Magnitude);             // S12: one way
            Assert.True(s.ComplexValues[(f * 2) * 2].Magnitude < 1e-15);               // S11: the matched transmit pin
        }

        // HB: an available power into the transmit pin, a matched load on the receive pin
        // HB solved to 1e-14 A: at its default tolerance the source's own voltage is 5e-7 off, 4e-6 dB of delivered power
        var (lib2, tb2) = Read($"""
            P1Tone:S1 tx 0 Freq=2.4e9 Pavl=10 Z=50 Ohm
            PropagationChannel:CH1 tx rx Distance={d}
            R:RL rx 0 R=50 Ohm
            analysis HB1 type=hb Tone=2.4e9 MaxHarm=3 Tol=1e-14
            """);
        using var nl2 = new Elaborator(lib2).Elaborate(tb2);
        var hba = tb2.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var r = new HbEngine(nl2, tb2).Run(HbEngine.Resolve(hba, nl2.ResolvedGlobals));
        var v = r.DataSet["V"];
        int node = Array.IndexOf(v.Axes[0].Labels!, "rx");
        var vrx = v.ComplexValues[node * v.Axes[1].Length + 1];
        double pDbm = 10 * Math.Log10(vrx.Magnitude * vrx.Magnitude / (2 * 50) / 1e-3);
        double closed = 10 + 20 * Math.Log10(PropagationMath.C / 2.4e9 / (4 * Math.PI * d));
        int ntx = Array.IndexOf(v.Axes[0].Labels!, "tx");
        var vtx = v.ComplexValues[ntx * v.Axes[1].Length + 1];
        output.WriteLine($"HB delivered {pDbm:G12} dBm, closed form {closed:G12} dBm; |V(tx)| {vtx.Magnitude:G12}, a1·√50 {Math.Sqrt(2e-2) * Math.Sqrt(50):G12}, ratio {(vrx / vtx).Magnitude:G15}");
        Assert.Equal(closed, pDbm, 1e-9);
        Assert.True(r.DataSet.Contains("link:CH1:PathLoss_dB"));
    }

    /// <summary>A PA (an amplifier with a third-order intercept) driving a channel into a linear LNA, read at IProbes.</summary>
    private static string Link(string channel, double lnaGain = 15, string pa = "Gain=20 IP3=20") => $"""
        MOD_Source:S1 s 0 Fc=2e9 Pavg=-10 Z=50 Ohm Carrier[1]="LTE;BW=1.4MHz;Mod=QPSK;Off=0;P=0;Seed=2"
        Amp:PA s 0 a 0 {pa}
        IProbe:Itx a tx
        PropagationChannel:CH1 tx rx {channel}
        Amp:LNA rx 0 b 0 Gain={lnaGain} IP3=200
        IProbe:Irx b c
        R:RL c 0 R=50 Ohm Noise=no
        analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=2e-6 Stop=40e-6
        """;

    private static (EnvelopeRunResult Run, EnvelopeRunner.Plan Plan) Record(string cnl, Func<EnvelopeParams, EnvelopeParams>? with = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        if (with is not null) plan = plan with { Params = with(plan.Params) };
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        return (EnvelopeRunner.Record(plan, Elab).Record, plan);
    }

    private static int Probe(EnvelopeRunResult r, string name) => Array.IndexOf(r.IProbeNames, name);

    [Fact]
    public void Gate2_ExplicitEqualsJoint()
    {
        const string ch = "Distance=40 TxGain=10 RxGain=6";
        var (joint, _) = Record(Link(ch), p => p with { RecordChannelWaves = true });
        var rec = Assert.Single(joint.ChannelRecordings!);
        // the receive side alone: the source kept on a dummy load (it sets the record and the rate), the channel's transmit pin
        // on a net of its own, driven by the recording
        string alone = """
            MOD_Source:S1 s 0 Fc=2e9 Pavg=-10 Z=50 Ohm Carrier[1]="LTE;BW=1.4MHz;Mod=QPSK;Off=0;P=0;Seed=2"
            R:RD s 0 R=50 Ohm Noise=no
            PropagationChannel:CH1 tx rx Distance=40 TxGain=10 RxGain=6
            Amp:LNA rx 0 b 0 Gain=15 IP3=200
            IProbe:Irx b c
            R:RL c 0 R=50 Ohm Noise=no
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=2e-6 Stop=40e-6
            """;
        var (rx, _) = Record(alone, p => p with { ChannelDrive = new Dictionary<string, ChannelRecording> { [rec.Key] = rec.Value } });
        int a = Probe(joint, "Irx"), b = Probe(rx, "Irx");
        double worst = 0, peak = 0;
        for (int t = 0; t < joint.Time.Length; t++)
        {
            var x = joint.IProbeCurrent[t][a][1]; var y = rx.IProbeCurrent[t][b][1];
            worst = Math.Max(worst, (x - y).Magnitude); peak = Math.Max(peak, x.Magnitude);
        }
        output.WriteLine($"worst difference {worst:G3} A over a peak of {peak:G3} A ({worst / peak:G3})");
        Assert.True(peak > 0);
        Assert.True(worst <= 1e-12 * peak, $"relative {worst / peak:G3}");
    }

    /// <summary>The spectrum of a probe's harmonic-1 current over the recorded window: (bin spacing, power per bin).</summary>
    private static (double Df, double[] P) Spectrum(EnvelopeRunResult r, string probe, Func<Complex, Complex>? map = null)
    {
        int p = Probe(r, probe), n = r.Time.Length - 1;          // whole records: the last sample is the first again
        var x = new Complex[n];
        for (int t = 0; t < n; t++) x[t] = (map ?? (v => v))(r.IProbeCurrent[t][p][1]);
        var X = Fft.Forward(x);
        return (1 / (n * r.StepS), [.. X.Select(v => v.Magnitude * v.Magnitude)]);
    }

    private static string RayList(string dir, string rows)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "rays.csv");
        File.WriteAllText(path, "delay (ns), power (dB), phase (deg), doppler (Hz)\n" + rows);
        return path;
    }

    [Fact]
    public void Gate3_Doppler_ShiftsTheCarrier_AndTwoRaysBeat()
    {
        string dir = Dir;
        try
        {
            string Bench(string rays) => $"""
                MOD_Source:S1 s 0 Fc=2e9 Pavg=-10 Z=50 Ohm Record=1e-3 Carrier[1]="CW;BW=100kHz;Off=0;P=0"
                PropagationChannel:CH1 s rx Distance=10 Multipath=file File="{RayList(dir, rays)}"
                IProbe:Irx rx c
                R:RL c 0 R=50 Ohm Noise=no
                analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=10e-6 Stop=4e-3
                """;
            var (one, _) = Record(Bench("0, 0, 0, 1000\n"));
            var (df, p) = Spectrum(one, "Irx");
            int peak = Array.IndexOf(p, p.Max());
            double at = (peak <= p.Length / 2 ? peak : peak - p.Length) * df;
            output.WriteLine($"one ray at 1 kHz: peak at {at} Hz (bin {df} Hz)");
            Assert.True(Math.Abs(at - 1000) <= df, $"peak at {at} Hz");

            var (two, _) = Record(Bench("0, 0, 0, 300\n40, 0, 0, -200\n"));
            var (df2, p2) = Spectrum(two, "Irx", v => v.Magnitude * v.Magnitude);
            p2[0] = 0;                                                  // the mean of |r|²
            int beat = Array.IndexOf(p2, p2.Max());
            double fb = Math.Min(beat, p2.Length - beat) * df2;
            output.WriteLine($"two rays at +300 and -200 Hz: |r|² beats at {fb} Hz");
            Assert.True(Math.Abs(fb - 500) <= df2, $"beat at {fb} Hz");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Gate4_TwoRayResponse_IsHofF_AcrossTheBand()
    {
        string dir = Dir;
        try
        {
            // 32 steps a sample of the 7.68 MS/s signal: quadratic Lagrange's own error at the band edge, ~(ωh)³/16, is below
            // 1e-6 there; at the run's automatic step it is ~1e-4 (the interpolation, not the channel)
            double h = 1.0 / (7.68e6 * 32), tau2 = 3.37 * h;
            string rays = RayList(dir, $"0, 0, 0, 0\n{(tau2 * 1e9).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, -3, 70, 0\n");
            string cnl = $"""
                MOD_Source:S1 s 0 Fc=2e9 Pavg=-10 Z=50 Ohm Record=0.5e-3 Shaping=off Carrier[1]="LTE;BW=1.4MHz;Mod=QPSK;Off=0;P=0;Seed=4"
                IProbe:Itx s tx
                PropagationChannel:CH1 tx rx Distance=30 Multipath=file File="{rays}"
                IProbe:Irx rx c
                R:RL c 0 R=50 Ohm Noise=no
                analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=5e-6 Stop=0.5e-3 Step={h.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}
                """;
            var (r, _) = Record(cnl);
            int n = r.Time.Length - 1;
            int a = Probe(r, "Itx"), b = Probe(r, "Irx");
            var v1 = new Complex[n]; var v2 = new Complex[n];
            for (int t = 0; t < n; t++) { v1[t] = r.IProbeVoltage[t][a][1]; v2[t] = r.IProbeVoltage[t][b][1]; }
            var V1 = Fft.Forward(v1); var V2 = Fft.Forward(v2);
            double top = V1.Max(v => v.Magnitude), df = 1 / (n * r.StepS);
            var (lib, tb) = Read(cnl);
            using var nl = new Elaborator(lib).Elaborate(tb);
            var ch = (PropagationChannelModel)nl.Components.Single(c => c.Model is PropagationChannelModel).Model;
            ch.SetCarrier(2e9);
            double worst = 0, scale = 0; int used = 0;
            for (int k = 0; k < n; k++)
            {
                if (V1[k].Magnitude < 1e-3 * top) continue;
                double off = (k <= n / 2 ? k : k - n) * df;
                var expect = ch.H(2e9 + off) * Complex.FromPolarCoordinates(1, -2 * Math.PI * off * r.StepS);
                worst = Math.Max(worst, (V2[k] / V1[k] - expect).Magnitude);
                scale = Math.Max(scale, expect.Magnitude);
                used++;
            }
            output.WriteLine($"{used} bins across ±{0.7} MHz: worst |T − H| {worst:G3} against |H| {scale:G3} ({worst / scale:G3})");
            Assert.True(used > 100);
            Assert.True(worst <= 1e-6 * scale, $"relative {worst / scale:G3}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>R-pch6-8: Reverse=CH1 is CH1's realization with its ends exchanged; a two-ray path between unequal heights is
    /// reciprocal, so its frozen H is CH1's to round-off. A name that is no channel is refused, naming the channels there are.</summary>
    [Fact]
    public void Reverse_IsTheSameRealizationTransposed()
    {
        var (lib, tb) = Read("""
            PropagationChannel:CH1 a b Distance=300 PathLoss=tworay TxHeight=25 RxHeight=1.5
            PropagationChannel:CH2 c d Reverse=CH1
            R:R1 b 0 R=50 Ohm
            R:R2 d 0 R=50 Ohm
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ch = nl.Components.Where(c => c.Model is PropagationChannelModel).Select(c => (PropagationChannelModel)c.Model).ToArray();
        foreach (double hz in new[] { 0.9e9, 2.4e9 })
            Assert.True((ch[0].H(hz) - ch[1].H(hz)).Magnitude < 1e-12 * ch[0].H(hz).Magnitude);
        var fwd = ch[0].At(2.4e9).Rays[1]; var rev = ch[1].At(2.4e9).Rays[1];
        Assert.Equal(fwd.Departure, rev.ArrivalFrom);
        Assert.Equal(fwd.ArrivalFrom, rev.Departure);

        var (lib2, tb2) = Read("""
            PropagationChannel:CH1 a b Distance=300
            PropagationChannel:CH2 c d Reverse=CH9
            """);
        var ex = Assert.Throws<ParameterRefusalException>(() => new Elaborator(lib2).Elaborate(tb2));
        Assert.Contains("Reverse=CH9 names no Propagation Channel", ex.Message);

        // a length in "m" is milli, a thousand times too small: refused, naming the spellings that work
        var (lib3, tb3) = Read("PropagationChannel:CH1 a b Distance=150 m\n");
        var milli = Assert.Throws<ParameterRefusalException>(() => new Elaborator(lib3).Elaborate(tb3));
        Assert.Contains("Distance=150 metre", milli.Message);
    }

    [Fact]
    public void Gate5_OneWay_TheTransmitSideIgnoresTheReceiveSide()
    {
        const string ch = "Distance=40";
        var (a, _) = Record(Link(ch, lnaGain: 15));
        var (b, _) = Record(Link(ch, lnaGain: 30));
        int pa = Probe(a, "Itx"), pb = Probe(b, "Itx");
        for (int t = 0; t < a.Time.Length; t++)
            Assert.Equal(a.IProbeCurrent[t][pa][1], b.IProbeCurrent[t][pb][1]);
        int ra = Probe(a, "Irx"), rb = Probe(b, "Irx");
        Assert.NotEqual(a.IProbeCurrent[^1][ra][1], b.IProbeCurrent[^1][rb][1]);
    }
}
