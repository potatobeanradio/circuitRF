using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief NZ-7's gates: thermal noise and a source's phase-noise mask in an envelope run. Every circuit is linear but the
/// mixer's multiplier, and every statistical bound is computed from the record it is read on, not fixed.
/// </summary>
public sealed class EnvelopeNoiseTests(ITestOutputHelper output)
{
    private const double K = 1.380649e-23, T0 = 290.0;

    private static DataSet Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        return EnvelopeRunner.Run(plan, Elab);
    }

    /// <summary>One IProbe's current envelope at <paramref name="harmonic"/> over the measured window.</summary>
    private static Complex[] Current(DataSet ds, int harmonic)
    {
        var c = ds["I"];
        int nt = c.Axes[0].Length, nb = c.Axes[1].Length, nk = c.Axes[2].Length;
        int t0 = (int)Math.Round(ds["__EnvMeasureFrom_s"].RealValues[0] / ds["__EnvStep_s"].RealValues[0]);
        var y = new Complex[nt - t0];
        for (int t = t0; t < nt; t++) y[t - t0] = c.ComplexValues[(t * nb + 0) * nk + harmonic];
        return y;
    }

    /// <summary>The whole recorded current at <paramref name="harmonic"/>, no settle skipped.</summary>
    private static Complex[] Recorded(DataSet ds, int harmonic)
    {
        var c = ds["I"];
        int nt = c.Axes[0].Length, nb = c.Axes[1].Length, nk = c.Axes[2].Length;
        return [.. Enumerable.Range(0, nt).Select(t => c.ComplexValues[(t * nb + 0) * nk + harmonic])];
    }

    private static Complex[] LessMean(Complex[] x)
    {
        Complex m = 0;
        foreach (var v in x) m += v;
        m /= x.Length;
        return [.. x.Select(v => v - m)];
    }

    // A source resistor at T0 in front of the two-port, the load noiseless: the output's available noise is k·T0·F·G.
    private static string Linear(string twoPort, string env) => $"""
        temp = 16.85
        MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=1e-3 Record=2e-3 Carrier[1]="CW;BW=0.2MHz;Off=0;P=0"
        R:RS s a R=50 Ohm
        {twoPort}
        IProbe:Iout b c
        R:RL c 0 R=50 Ohm Noise=no
        analysis ENV1 type=env Accuracy=standard MaxHarm=1 Segments=1 Warmup=0 Stop=2e-3 {env}
        """;

    private const string Pad = """
        R:R1 a 0 R=96.248 Ohm
        R:R2 a b R=71.151 Ohm
        R:R3 b 0 R=96.248 Ohm
        """;

    private const string Amp = "Amp:A1 a 0 b 0 Gain=20 IP3=200 NF=3";

    /// <summary>
    /// Gate 1, the linear limit: the delivered noise density into the matched load, away from the tone, is k·T0·F·G — 1
    /// for a matched pad at T0 (F·G = L·1/L), 10^0.3·100 for an amplifier with NF=3 and 20 dB of gain. Read twice: as the
    /// variance of the load current (white across the band, so N samples give a relative standard error of 1/√N), and
    /// off the run's own PSD cube.
    /// </summary>
    [Theory]
    [InlineData("pad", 1.0)]
    [InlineData("amp", 199.526231496888)]   // 10^0.3 · 100
    public void Gate1_TheFloorIsKT0FG(string which, double fg)
    {
        var ds = Run(Linear(which == "pad" ? Pad : Amp, "Noise=yes"));
        double fs = 1.0 / ds["__EnvStep_s"].RealValues[0];
        var i = LessMean(Current(ds, 1));
        double p = 0.5 * 50 * i.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary);   // W, over the band fs
        double density = p / fs, expect = K * T0 * fg;
        double se = 1 / Math.Sqrt(i.Length);
        output.WriteLine($"{which}: {10 * Math.Log10(density * 1e3):F3} dBm/Hz, expected {10 * Math.Log10(expect * 1e3):F3}; " +
                         $"ratio {density / expect:F4}, standard error {se:F4} over {i.Length} samples");
        Assert.True(Math.Abs(density / expect - 1) < 4 * se,
                    $"{which}: ratio {density / expect:F4} is more than 4 standard errors ({4 * se:F4}) from 1");

        // The PSD cube, away from the tone: the same floor, its mean over the bins within 0.4·fs of the carrier.
        var psd = ds["PSD"];
        var f = psd.Axes[1].Values;
        double bin = f[1] - f[0];
        var away = Enumerable.Range(0, f.Length)
                             .Where(k => Math.Abs(f[k] - 1e9) > 20 * bin && Math.Abs(f[k] - 1e9) < 0.4 * fs).ToList();
        double mean = away.Average(k => Math.Pow(10, psd.RealValues[k] / 10) * 1e-3);
        output.WriteLine($"{which}: PSD cube {10 * Math.Log10(mean * 1e3):F3} dBm/Hz over {away.Count} bins");
        Assert.True(Math.Abs(10 * Math.Log10(mean / expect)) < 0.2, $"{which}: PSD cube floor {10 * Math.Log10(mean * 1e3):F3} dBm/Hz");
    }

    /// <summary>
    /// A source's own impedance with Noise=yes (the NZ-7 follow-up) is thermal at the source's temperature: a 50 Ω
    /// MOD_Source or P1Tone at T0 driving the matched pad, with no resistor of its own, gives the pad's k·T0 floor; the same
    /// source without the key gives the pad's own noise only, k·T0·(1 − 1/L) with L = 10.
    /// </summary>
    [Theory]
    [InlineData("MOD_Source:S1 a 0 Fc=1e9 Pavg=-30 Z=50 Record=2e-3 Carrier[1]=\"CW;BW=0.2MHz;Off=0;P=0\"", "S1")]
    [InlineData("MOD_Source:S0 x 0 Fc=1e9 Pavg=-200 Z=50 Record=2e-3 Carrier[1]=\"CW;BW=0.2MHz;Off=0;P=0\"\nR:R0 x 0 R=50 Ohm Noise=no\nP1Tone:P1 a 0 Num=1 Pavl=-30 dBm Z=50 Ohm Freq=1 GHz", "P1")]
    public void ANoisySource_IsThermalAtItsTemperature(string source, string name)
    {
        double Floor(bool noisy)
        {
            string line = noisy
                ? string.Join('\n', source.Split('\n').Select(l => l.StartsWith(name == "S1" ? "MOD_Source:S1" : "P1Tone") ? l + " Noise=yes Temp=16.85" : l))
                : source;
            var ds = Run($"""
                temp = 16.85
                {line}
                {Pad}
                IProbe:Iout b c
                R:RL c 0 R=50 Ohm Noise=no
                analysis ENV1 type=env Accuracy=standard MaxHarm=1 Segments=1 Warmup=0 Stop=2e-3 Noise=yes
                """);
            double fs = 1.0 / ds["__EnvStep_s"].RealValues[0];
            var i = LessMean(Current(ds, 1));
            return 0.5 * 50 * i.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary) / fs / (K * T0);
        }
        double with = Floor(true), without = Floor(false);
        double se = 1 / Math.Sqrt(15000);
        output.WriteLine($"{name}: noisy {with:F4} k·T0, noiseless {without:F4} k·T0 (expected 1 and 0.9), standard error {se:F4}");
        Assert.True(Math.Abs(with - 1) < 4 * se);
        Assert.True(Math.Abs(without - 0.9) < 4 * se * 0.9);
    }

    /// <summary>
    /// The two-fundamental engine injects the same noise: two CW carriers 400 MHz apart, each its own fundamental, through
    /// the matched pad at T0. At the second carrier's product the load current's floor is k·T0 again.
    /// </summary>
    [Fact]
    public void Lattice_TheFloorIsKT0()
    {
        var ds = Run($"""
            temp = 16.85
            MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=1e-3 Record=0.5e-3 Carrier[1]="CW;BW=0.2MHz;Off=0;P=0"
            MOD_Source:S2 s2 0 Fc=1.4e9 Pavg=-30 Z=1e-3 Record=0.5e-3 Carrier[1]="CW;BW=0.2MHz;Off=0;P=0"
            R:RS s a R=50 Ohm
            R:R9 s2 0 R=50 Ohm
            {Pad}
            IProbe:Iout b c
            R:RL c 0 R=50 Ohm Noise=no
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 MaxMixOrder=2 Envelope=lattice Segments=1 Warmup=0 Stop=0.5e-3 Noise=yes
            """);
        Assert.Equal("mixIndex", ds["I"].Axes[2].Name);
        double fs = 1.0 / ds["__EnvStep_s"].RealValues[0];
        var freqs = ds["I"].Axes[2].Values;
        int m = Array.FindIndex(freqs, f => Math.Abs(Math.Abs(f) - 1.4e9) < 1);
        var i = LessMean(Current(ds, m));
        double density = 0.5 * 50 * i.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary) / fs;
        double se = 1 / Math.Sqrt(i.Length);
        output.WriteLine($"lattice product {freqs[m] / 1e9:G4} GHz: ratio {density / (K * T0):F4}, standard error {se:F4}");
        Assert.True(Math.Abs(density / (K * T0) - 1) < 4 * se);
    }

    private const string Mask = "10kHz:-70, 100kHz:-90, 300kHz:-100";

    // A CW P1Tone carrying the mask into a matched load; the MOD_Source only sets the fundamental and is off.
    private static string Tone(int seed) => $"""
        MOD_Source:S0 x 0 Fc=1e9 Pavg=-200 Z=50 Record=8e-3 Carrier[1]="CW;BW=0.2MHz;Off=0;P=0"
        R:R0 x 0 R=50 Ohm
        P1Tone:P1 a 0 Num=1 Pavl=0 dBm Z=50 Ohm Freq=1 GHz PhaseNoise="{Mask}"
        IProbe:Iout a b
        R:RL b 0 R=50 Ohm
        analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=0 Stop=8e-3 Seed={seed}
        """;

    /// <summary>
    /// Gate 2, the mask comes back: the load current's spectrum around the carrier, relative to the carrier, averaged over
    /// 16 seeds, is L(f) to ±1 dB at every bin from 20 kHz (five bins) to 0.4·fs, both sides.
    /// </summary>
    [Fact]
    public void Gate2_TheMaskComesBack()
    {
        var mask = PhaseNoiseMask.Parse(Mask);
        const int Seeds = 16, Nseg = 512;
        // The seeds are independent runs: side by side.
        var each = new (double fs, double[] f, double[] d)[Seeds];
        Parallel.For(0, Seeds, s =>
        {
            var ds = Run(Tone(s + 1));
            double rate = 1.0 / ds["__EnvStep_s"].RealValues[0];
            var i = Current(ds, 1);
            double carrier = i.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary);
            var (ff, d) = SignalMeasure.Welch(LessMean(i), rate, Nseg);
            each[s] = (rate, ff, [.. d.Select(x => x / carrier / Seeds)]);
        });
        double fs = each[0].fs;
        var f = each[0].f;
        var sum = new double[f.Length];
        foreach (var e in each) for (int k = 0; k < sum.Length; k++) sum[k] += e.d[k];
        double worst = 0, at = 0, meanErr = 0;
        int n = 0;
        for (int k = 0; k < f.Length; k++)
        {
            double off = Math.Abs(f[k]);
            if (off < 20e3 || off > 0.4 * fs) continue;
            double err = 10 * Math.Log10(sum[k]) - mask.At(off);
            meanErr += err;
            if (Math.Abs(err) > Math.Abs(worst)) { worst = err; at = f[k]; }
            n++;
        }
        output.WriteLine($"{n} bins of {fs / Nseg / 1e3:F2} kHz; worst {worst:+0.00;-0.00} dB at {at / 1e3:F1} kHz");
        Assert.True(n > 100);
        Assert.True(Math.Abs(worst) < 1.0, $"the spectrum is {worst:F2} dB from the mask at {at / 1e3:F1} kHz");
        output.WriteLine($"mean error {meanErr / n:+0.000;-0.000} dB");
    }

    // An ideal multiplier: the LO (a P1Tone at f0 with the mask) times a CW blocker 300 kHz above it. The sum product,
    // harmonic 2, carries the blocker at +300 kHz with the LO's phase noise around it, one-sided.
    private static string Mixer(int seed) => $"""
        MOD_Source:S1 rf 0 Fc=1e9 Pavg=-20 Z=50 Record=8e-3 Carrier[1]="CW;BW=0.1MHz;Off=300e3;P=0"
        R:RR rf 0 R=50 Ohm
        P1Tone:LO lo 0 Num=1 Pavl=0 dBm Z=50 Ohm Freq=1 GHz PhaseNoise="{Mask}"
        R:RLO lo 0 R=50 Ohm
        SDD:X1 lo 0 rf 0 out 0 I[1,0]=0 I[2,0]=0 I[3,0]=-0.02*_v1*_v2
        IProbe:Iout out b
        R:RL b 0 R=50 Ohm
        analysis ENV1 type=env Accuracy=draft MaxHarm=2 Segments=1 Warmup=0 Stop=8e-3 Seed={seed}
        """;

    /// <summary>
    /// Gate 3, reciprocal mixing: the wanted channel is five whole bins (B = 18.75 kHz) centred Δ = 198.75 kHz below the
    /// blocker's product; the in-channel output noise is P_blocker + L(Δ) + 10·log10(B), to ±1 dB, averaged over 8 seeds.
    /// </summary>
    [Fact]
    public void Gate3_ReciprocalMixing()
    {
        var mask = PhaseNoiseMask.Parse(Mask);
        const double Blocker = 300e3;
        const int Seeds = 8, Nseg = 512, Centre = 27, Half = 2;   // bins of fs/Nseg = 3.75 kHz at draft
        var ratios = new double[Seeds];
        var bins = new double[Seeds];
        Parallel.For(0, Seeds, s =>                                 // independent runs, side by side
        {
            var ds = Run(Mixer(s + 1));
            double fs = 1.0 / ds["__EnvStep_s"].RealValues[0];
            var y = Current(ds, 2);
            double pBlocker = y.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary);
            var (f, d) = SignalMeasure.Welch(y, fs, Nseg);
            double bw = fs / Nseg, inChannel = 0;
            for (int k = 0; k < f.Length; k++)
                if (Math.Abs(f[k] / bw - Centre) <= Half + 1e-9) inChannel += d[k] * bw;
            ratios[s] = inChannel / pBlocker;
            bins[s] = bw;
        });
        double ratio = ratios.Average(), bin = bins[0];
        double delta = Blocker - Centre * bin, b = (2 * Half + 1) * bin;
        double got = 10 * Math.Log10(ratio), expect = mask.At(delta) + 10 * Math.Log10(b);
        output.WriteLine($"channel {b / 1e3:F2} kHz at {delta / 1e3:F2} kHz from the blocker: in-channel noise {got:F2} dBc, " +
                         $"closed form {expect:F2} dBc");
        Assert.True(Math.Abs(got - expect) < 1.0, $"in-channel {got:F2} dBc against {expect:F2} dBc");
    }

    /// <summary>
    /// Gate 4, determinism: the same seed gives the same bits, run to run and across a split into parallel segments (the
    /// draws are keyed by the absolute sample, so a segment draws what the sequential run draws there); another seed
    /// changes the noise and leaves the noiseless run under it alone — the run is linear, so each noisy run less the
    /// noiseless one is pure noise, of the same power.
    /// </summary>
    [Fact]
    public void Gate4_SameSeedSameBits()
    {
        string Bench(string keys) => Linear(Pad, keys).Replace("RS s a R=50 Ohm", "RS s a R=50 Ohm\nP1Tone:P2 s2 0 Num=2 Pavl=-10 dBm Z=50 Ohm Freq=1 GHz PhaseNoise=\"" + Mask + "\"\nR:R9 s2 0 R=50 Ohm");
        var a = Current(Run(Bench("Noise=yes Seed=7")), 1);
        var b = Current(Run(Bench("Noise=yes Seed=7")), 1);
        Assert.Equal(a, b);

        var quiet = Current(Run(Linear(Pad, "")), 1);
        var c = Current(Run(Linear(Pad, "Noise=yes Seed=8")), 1);
        var d = Current(Run(Linear(Pad, "Noise=yes Seed=9")), 1);
        Assert.NotEqual(c, d);
        double Power(Complex[] x, Complex[] q) => x.Zip(q, (u, v) => (u - v).Magnitude * (u - v).Magnitude).Average();
        double pc = Power(c, quiet), pd = Power(d, quiet), se = Math.Sqrt(2.0 / c.Length);
        output.WriteLine($"noise power ratio {pc / pd:F4}, standard error {se:F4}");
        Assert.True(Math.Abs(pc / pd - 1) < 4 * se);
        Assert.True(Math.Abs((c.Average(v => v.Real) - quiet.Average(v => v.Real)) / Math.Sqrt(pc)) < 4 / Math.Sqrt(c.Length));
    }

    /// <summary>
    /// Gate 4, the segment half: two segments with a warm-up draw, sample for sample, what one segment draws — bit for bit
    /// on this memoryless circuit. Sample 0 is left out: the one-segment run starts cold there, at its noiseless
    /// harmonic-balance point, where the split run is already a warm-up into its record.
    /// </summary>
    [Fact]
    public void Gate4_SegmentsDrawWhatOneRunDraws()
    {
        var one = Recorded(Run(Linear(Pad, "Noise=yes Seed=3")), 1);
        var two = Recorded(Run(Linear(Pad, "Noise=yes Seed=3").Replace("Segments=1 Warmup=0", "Segments=2 Warmup=1e-6")), 1);
        int n = Math.Min(one.Length, two.Length);
        double worst = 0, rms = Math.Sqrt(one.Average(v => v.Magnitude * v.Magnitude));
        int at = -1;
        for (int t = 1; t < n; t++)
            if ((one[t] - two[t]).Magnitude > worst) { worst = (one[t] - two[t]).Magnitude; at = t; }
        output.WriteLine($"worst sample difference {worst / rms:E2} of the RMS, at sample {at} of {n}");
        Assert.True(worst < 1e-9 * rms);
    }

    /// <summary>
    /// Gate 5, no key, no change: with Noise absent and no mask, the seed reaches nothing (a run at another seed is
    /// bit-identical), and the directive writes back without a noise key.
    /// </summary>
    [Fact]
    public void Gate5_NoKeyNoChange()
    {
        var a = Run(Linear(Pad, ""));
        var b = Run(Linear(Pad, "Seed=12345"));
        Assert.Equal(Current(a, 1), Current(b, 1));
        Assert.Equal(a["PSD"].RealValues, b["PSD"].RealValues);

        var (_, tb) = new CnlReader().Read(Linear(Pad, ""));
        string line = CnlWriter.Write(tb).Split('\n').Single(l => l.Contains("type=env"));
        Assert.DoesNotContain("Noise", line);
        Assert.DoesNotContain("Seed", line);
    }

    /// <summary>A parametric sweep's points share the seed unless SeedPerPoint says otherwise; then each point (named by
    /// its drive, outer points included) gets its own, the same one on every run.</summary>
    [Fact]
    public void SeedPerPoint_GivesEachPointItsOwnSeed()
    {
        EnvelopeRunner.Plan PlanOf(string keys)
        {
            var (_, tb) = new CnlReader().Read(Linear(Pad, keys));
            return EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), new Dictionary<string, Core.Expressions.Value>());
        }
        var shared = PlanOf("Noise=yes Seed=4");
        Assert.Equal(4UL, shared.ForPoint("Pin=0").Params.Seed);
        Assert.Equal(4UL, shared.ForPoint("Pin=1").Params.Seed);

        var own = PlanOf("Noise=yes Seed=4 SeedPerPoint=yes");
        Assert.NotEqual(own.ForPoint("Pin=0").Params.Seed, own.ForPoint("Pin=1").Params.Seed);
        Assert.Equal(own.ForPoint("Pin=1").Params.Seed, PlanOf("Noise=yes Seed=4 SeedPerPoint=yes").ForPoint("Pin=1").Params.Seed);
    }

    /// <summary>The keys round-trip through the netlist writer, each only when it differs from its default.</summary>
    [Fact]
    public void Keys_RoundTrip()
    {
        var (_, tb) = new CnlReader().Read(Linear(Pad, "Noise=yes Seed=-4 SeedPerPoint=yes"));
        var (_, back) = new CnlReader().Read(CnlWriter.Write(tb));
        var env = back.Analyses.OfType<EnvelopeAnalysis>().Single();
        Assert.True(env.Noise);
        Assert.Equal("-4", env.SeedExpr);
        Assert.True(env.SeedPerPoint);
    }

    /// <summary>
    /// The draws are the same BITS on every platform: they are pure arithmetic (no platform maths library), so these
    /// values, fixed once, must come back exactly wherever the suite runs.
    /// </summary>
    [Fact]
    public void Draws_AreFixedBits()
    {
        ulong key = NoiseDraw.Key(1, NoiseDraw.Hash("thermal:R1"));
        long[] bits = [.. Enumerable.Range(0, 3).SelectMany(i =>
        {
            var z = NoiseDraw.Complex(key, i);
            return new[] { BitConverter.DoubleToInt64Bits(z.Real), BitConverter.DoubleToInt64Bits(z.Imaginary) };
        })];
        output.WriteLine(string.Join(", ", bits.Select(b => $"0x{b:X16}")));
        Assert.Equal(Golden, bits);
    }

    private static readonly long[] Golden =
    [
        0x3FA9B09B0B7D13D7, 0x3FF2920AA0760A49, 0x3FD024C42881EE8D,
        0x3FD2873D3DD18706, unchecked((long)0xBFD8E51017216D0D), unchecked((long)0xBFED8D53B931C5B1),
    ];
}
