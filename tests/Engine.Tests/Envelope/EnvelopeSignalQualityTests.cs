using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief NZ-8's gates: EVM, its split into distortion and noise, the SNR and the constellation of an envelope run, read
/// at an IProbe. Every circuit is a matched pad, so what EVM reads is the source's own signal plus the noise the run adds,
/// and every statistical bound is computed from the record it is read on.
/// </summary>
public sealed class EnvelopeSignalQualityTests(ITestOutputHelper output)
{
    private const double K = 1.380649e-23, T0 = 290.0;

    private static (DataSet ds, ModulatedSignal signal) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var ds = EnvelopeRunner.Run(plan, Elab);
        return (ds, nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single().Signal);
    }

    // A 50 Ω source resistor at T0, the pad, and a noiseless 50 Ω load: the probe node's voltage noise is 4kT0·50/4 V²/Hz.
    private static string Pad(string source, string env, string acc = "draft") => $"""
        temp = 16.85
        {source}
        R:RS s a R=50 Ohm
        R:R1 a 0 R=96.248 Ohm
        R:R2 a b R=71.151 Ohm
        R:R3 b 0 R=96.248 Ohm
        IProbe:Iout b c
        R:RL c 0 R=50 Ohm Noise=no
        analysis ENV1 type=env Accuracy={acc} MaxHarm=1 Segments=1 Warmup=1e-6 Stop=0.5e-3 {env}
        """;

    private const string Lte = "Carrier[1]=\"LTE;BW=5MHz;Mod=64QAM;Off=0;P=0;Seed=3\"";

    private static double At(DataSet ds, string cube) => ds[cube].RealValues[0];

    /// <summary>
    /// Gate 1: distortion- and noise-free, the pad reads the source signal's own EVM — the Signal Designer's, SignalEvm on
    /// the record the source plays — shaped only (~0.25 %) and clipped (~5 %). To 1e-5 percentage points, not the brief's
    /// 1e-6: the run plays the record interpolated to the envelope rate by a windowed sinc, and the demodulated points
    /// differ from the native record's by 6.9e-8 rms (measured, at 1x and 4x alike), which moves EVM by up to that times
    /// 100 pp whatever its size (measured 3.4e-6 and 9.4e-6 pp). The demodulation itself is exact:
    /// SignalEvmTests.E6 reads the same record resampled exactly.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Clip=7")]
    public void Gate1_APad_ReadsTheSourcesOwnEvm(string clip)
    {
        var (ds, sig) = Run(Pad($"MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=1e-3 Record=0.5e-3 {clip} {Lte}", ""));
        var own = SignalEvm.Measure(sig.Carriers[0].Spec, sig.Carriers[0].Native, sig.RecordSeconds);
        double evm = At(ds, "EVM_pct");
        output.WriteLine($"envelope {evm:F9} %, Signal Designer {own.RmsPercent:F9} %, peak {At(ds, "EVMpeak_pct"):F4} / {own.PeakPercent:F4}");
        Assert.InRange(own.RmsPercent, 0.1, 10);
        Assert.Equal(own.RmsPercent, evm, 1e-5);
        Assert.Equal(-20 * Math.Log10(evm / 100), At(ds, "MER_dB"), 1e-9);

        // the constellation: the equalised points over the ideal ones, decimated, the two cubes paired point for point
        var c = ds["Const"]; var r = ds["ConstRef"];
        Assert.Equal(3, c.Rank);
        Assert.InRange(c.Axes[2].Length, 1, EnvelopeDataSet.ConstMaxSamples);
        Assert.Equal(r.Axes[1].Length, c.Axes[2].Length);
        double err = 0, den = 0;
        for (int i = 0; i < r.ComplexValues.Length; i++)
        {
            err += (c.ComplexValues[i] - r.ComplexValues[i]).Magnitude * (c.ComplexValues[i] - r.ComplexValues[i]).Magnitude;
            den += r.ComplexValues[i].Magnitude * r.ComplexValues[i].Magnitude;
        }
        Assert.InRange(100 * Math.Sqrt(err / den), 0.8 * evm, 1.2 * evm);
        Assert.False(ds.Contains("SNR_dB"));                           // no EvmSplit, no SNR
    }

    /// <summary>
    /// Gate 2: additive noise. The pad with Noise=yes and an unclipped, unshaped signal: EVM² is the in-channel noise over
    /// the in-channel signal, k·T0·B / P_signal with B the occupied subcarriers' bandwidth (the probe voltage's noise is
    /// 50·k·T0 V²/Hz, its signal 50·P). Its relative standard error over N resource elements is 1/√N. And with EvmSplit,
    /// EVM is 1/√SNR, the SNR measured from the two runs.
    /// </summary>
    [Fact]
    public void Gate2_ThermalNoise_ReadsOneOverRootSnr()
    {
        var (ds, sig) = Run(Pad($"MOD_Source:S1 s 0 Fc=1e9 Pavg=-20 Z=1e-3 Record=0.5e-3 Shaping=off {Lte}", "Noise=yes EvmSplit=yes"));
        var nm = OfdmGenerator.Numerology(sig.Carriers[0].Spec);
        double fs = 1.0 / At(ds, "__EnvStep_s");
        double pSignal = At(ds, "Pprobe_fund_W") - K * T0 * fs;          // less the delivered noise across the band
        double b = nm.NSc * nm.ScsHz;
        double expect = Math.Sqrt(K * T0 * b / pSignal) * 100;
        double evm = At(ds, "EVM_pct");
        int res = nm.NSc * nm.SymbolsPerHalfMs;                           // one 0.5 ms record
        double se = 1 / Math.Sqrt(res);                                   // of EVM²
        double snr = Math.Pow(10, At(ds, "SNR_dB") / 10);
        output.WriteLine($"SNR {10 * Math.Log10(pSignal / (K * T0 * b)):F2} dB closed form, {At(ds, "SNR_dB"):F2} dB measured; " +
                         $"EVM {evm:F4} %, expected {expect:F4} %; EVM² ratio {evm * evm / (expect * expect):F4}, standard error {se:F4} over {res} REs");
        Assert.True(Math.Abs(evm * evm / (expect * expect) - 1) < 4 * se);
        Assert.Equal(1, evm / 100 * Math.Sqrt(snr), 0.01);
        Assert.InRange(At(ds, "EVMdist_pct"), 0, 1e-3);                  // no distortion: the quiet run reads ~0
    }

    /// <summary>
    /// Gate 3: a phase-noise mask on the source. Small-angle, the error a phase φ(t) adds is jφ·s: the part of φ slower
    /// than a symbol turns every subcarrier alike (common phase), the faster part spreads each subcarrier onto its
    /// neighbours at the offset f (inter-carrier), and of that only the share landing inside the occupied band B is
    /// read — 1 − f/B of it. With one gain fitted over the record (its mean phase removed), EVM² = Σ S_φ(f_m)/T·(1 − f_m/B)
    /// over the record's bins f_m = m/T, S_φ = 2·10^(L/10). Averaged over four seeds (each draw is Gaussian per bin), to
    /// ±10 %: the closed form's own accuracy, which neglects the spread of an off-grid component over several bins.
    /// </summary>
    [Fact]
    public void Gate3_PhaseNoise_MatchesTheClosedForm()
    {
        const string mask = "1kHz:-88, 400kHz:-88, 4MHz:-128, 15MHz:-150";
        double sum = 0;
        double closed = 0;
        for (int seed = 1; seed <= 4; seed++)
        {
            var (ds, sig) = Run(Pad($"MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=1e-3 Record=0.5e-3 Shaping=off PhaseNoise=\"{mask}\" {Lte}", $"Seed={seed}"));
            double e = At(ds, "EVM_pct") / 100;
            sum += e * e;
            if (seed == 1)
            {
                var nm = OfdmGenerator.Numerology(sig.Carriers[0].Spec);
                double t = sig.RecordSeconds, b = nm.NSc * nm.ScsHz, fs = 1.0 / At(ds, "__EnvStep_s");
                var m = PhaseNoiseMask.Parse(mask);
                for (int k = 1; k / t < fs / 2; k++)
                {
                    double f = k / t;
                    closed += 2 * Math.Pow(10, m.At(f) / 10) / t * Math.Max(0, 1 - f / b);
                }
            }
        }
        double evm = Math.Sqrt(sum / 4), cf = Math.Sqrt(closed);
        output.WriteLine($"EVM {100 * evm:F4} % over four seeds, closed form {100 * cf:F4} %, ratio {evm / cf:F4}");
        Assert.InRange(evm / cf, 0.9, 1.1);
    }

    /// <summary>
    /// Gate 4: the split adds up. A clipped signal with noise: EVMdist² + EVMnoise² = EVM², unless the clamp set the noise
    /// to zero (then EVMdist ≥ EVM). And the noise part alone is what the SNR says.
    /// </summary>
    [Fact]
    public void Gate4_TheSplitAddsUp()
    {
        var (ds, _) = Run(Pad($"MOD_Source:S1 s 0 Fc=1e9 Pavg=-20 Z=1e-3 Record=0.5e-3 Clip=8 {Lte}", "Noise=yes EvmSplit=yes"));
        double e = At(ds, "EVM_pct"), d = At(ds, "EVMdist_pct"), n = At(ds, "EVMnoise_pct");
        output.WriteLine($"EVM {e:F4} %, distortion {d:F4} %, noise {n:F4} %, SNR {At(ds, "SNR_dB"):F2} dB");
        Assert.True(d > 0.3 && n > 0.3);
        if (n > 0) Assert.Equal(e * e, d * d + n * n, 1e-9 * e * e);
        else Assert.True(d >= e);
        Assert.Equal(n / 100, 1 / Math.Sqrt(Math.Pow(10, At(ds, "SNR_dB") / 10)), 0.1 * n / 100);
    }

    /// <summary>
    /// The noiseless run is as converged as the noisy one. An amplifier far below compression (its output -37 dBm, IP3 20
    /// dBm) only passes the clipped signal on, so its distortion EVM is the record's own. At the default Tol (1e-6 A, about
    /// 1 % of this probe's current) a noiseless run accepts the step predictor where the noisy one, whose noise the
    /// predictor never meets, always solves: without a Newton update on every noiseless step the split read 3.463 %
    /// distortion and no noise at all.
    /// </summary>
    [Fact]
    public void TheNoiselessRun_IsAsConvergedAsTheNoisyOne()
    {
        var (ds, sig) = Run($"""
            temp = 16.85
            MOD_Source:S1 s 0 Fc=2e9 Pavg=-10 Z=1e-3 Record=0.5e-3 Clip=8 {Lte}
            R:RS s a R=50 Ohm
            Amp:A1 a 0 b 0 Gain=20 IP3=20 NF=6
            IProbe:Iout b c
            R:RL c 0 R=50 Ohm Noise=no
            analysis ENV1 type=env Accuracy=draft MaxHarm=3 Segments=1 Warmup=1e-6 Stop=0.5e-3 Noise=yes EvmSplit=yes
            """);
        double own = SignalEvm.Measure(sig.Carriers[0].Spec, sig.Carriers[0].Native, sig.RecordSeconds).RmsPercent;
        output.WriteLine($"distortion {At(ds, "EVMdist_pct"):F4} %, the record's own {own:F4} %, noise {At(ds, "EVMnoise_pct"):F4} %");
        Assert.Equal(own, At(ds, "EVMdist_pct"), 0.01);
        Assert.True(At(ds, "EVMnoise_pct") > 0.5);
    }

    /// <summary>
    /// Gate 5: no key, no change. A CW envelope run with no noise writes none of the new cubes; a run without EvmSplit
    /// writes no split. (Byte identity of the existing cubes is the CLI check recorded with the brief.)
    /// </summary>
    [Fact]
    public void Gate5_NoLteNoNoise_WritesNothingNew()
    {
        var (ds, _) = Run(Pad("MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=1e-3 Record=0.5e-3 Carrier[1]=\"CW;BW=0.2MHz;Off=0;P=0\"", ""));
        foreach (var name in new[] { "EVM_pct", "EVM_dB", "EVMpeak_pct", "MER_dB", "Const", "ConstRef", "EVMdist_pct", "EVMnoise_pct", "SNR_dB",
                                     "FSKerr_pct", "FreqDev_Hz", "FreqOffset_Hz", "FreqPts", "FreqRef", "CSSerr_pct" })
            Assert.False(ds.Contains(name), name);
        Assert.True(ds.Contains("ACLR"));
    }

    // DBPSK, FSK and CSS through the pad, each over one record of its own: the cube each is measured into, and the run's
    // source power for an in-channel SNR in the twenties.
    public static TheoryData<string, string, double> Iot => new()
    {
        { "DBPSK;Rb=1kbps;Data=0110100111;Off=0;P=0", "EVM_pct", -105 },
        { "FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0", "FSKerr_pct", -82 },
        { "CSS;SF=7;BW=125kHz;Off=0;P=0", "CSSerr_pct", -86 },
    };

    private static string IotPad(string carrier, double pavg, string env, string stop) => $"""
        temp = 16.85
        MOD_Source:S1 s 0 Fc=868e6 Pavg={pavg} Z=50 Carrier[1]="{carrier}"
        R:R1 s 0 R=96.248 Ohm
        R:R2 s b R=71.151 Ohm
        R:R3 b 0 R=96.248 Ohm
        IProbe:Iout b c
        R:RL c 0 R=50 Ohm
        analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop={stop} {env}
        """;

    private static double RecordOf(string carrier) =>
        SignalComposer.Generate(new SignalSpec { Carriers = [SignalSpec.ParseCarrier(carrier)] }).RecordSeconds;

    /// <summary>
    /// The other carriers with symbols, through a matched pad. Noiseless, each reads zero error (the drive's own
    /// interpolation aside) and FSK its own deviation with no offset. With Noise=yes and EvmSplit the error is all noise:
    /// the distortion part ~0, and for DBPSK and CSS error² × SNR = 1 to the record's statistics (the SNR measured from
    /// the two runs on the same filtered samples).
    /// </summary>
    [Theory]
    [MemberData(nameof(Iot))]
    public void TheIotCarriers_ReadZeroThroughAPad_AndTheirNoiseAsTheSnrSays(string carrier, string cube, double pavg)
    {
        string stop = RecordOf(carrier).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var (clean, _) = Run(IotPad(carrier, pavg, "", stop));
        output.WriteLine($"{cube} noiseless {At(clean, cube):E3} %");
        Assert.True(At(clean, cube) < 1e-3, $"{cube} {At(clean, cube)} %");
        if (cube == "FSKerr_pct")
        {
            Assert.Equal(45e3, At(clean, "FreqDev_Hz"), 1e-2);
            Assert.Equal(0, At(clean, "FreqOffset_Hz"), 1e-2);
            Assert.True(clean.Contains("FreqPts") && clean.Contains("FreqRef"));
        }
        if (cube == "EVM_pct") Assert.True(clean.Contains("Const"));

        var (noisy, _) = Run(IotPad(carrier, pavg, "Noise=yes EvmSplit=yes", stop));
        string dist = cube switch { "EVM_pct" => "EVMdist_pct", "FSKerr_pct" => "FSKerrDist_pct", _ => "CSSerrDist_pct" };
        double e = At(noisy, cube) / 100, snr = Math.Pow(10, At(noisy, "SNR_dB") / 10);
        output.WriteLine($"{cube} {100 * e:F4} %, distortion {At(noisy, dist):E3} %, SNR {At(noisy, "SNR_dB"):F2} dB, error² x SNR {e * e * snr:F4}");
        Assert.True(At(noisy, dist) < 1e-3);
        Assert.InRange(e, 0.005, 0.5);
        if (cube != "FSKerr_pct") Assert.InRange(e * e * snr, 0.8, 1.2);
    }

    // A 60 MHz bandpass (third-order Butterworth, ~10 ns of group delay) between a 50 Ω source and a 50 Ω load, at draft,
    // over 0.5 ms (the NR record; half the DBPSK one, read in its whole bits). Its own distortion across a carrier within
    // ±10 MHz of its centre is under 1e-4, so a carrier reads the same EVM anywhere in that range.
    private static string Bandpass(string carrier) => $"""
        MOD_Source:S1 s 0 Fc=2.45e9 Pavg=-30 Z=50 Record=0.5e-3 Carrier[1]="{carrier}"
        Filter:BPF s 0 b 0 Response=Butterworth Form=Bandpass Order=3 F1=2.42 GHz F2=2.48 GHz IL=0 dB
        IProbe:Iout b c
        R:RL c 0 R=50 Ohm
        analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop=0.5e-3
        """;

    /// <summary>
    /// A carrier off the envelope's centre reads the EVM it reads at the centre (EnvelopeEngine.Oversample). Gear-2 reads
    /// an envelope frequency f as f·(1 + (2π f h)²/3), a phase error through the filter's delay that no delay fit takes
    /// out. The signal rate held an off-centre carrier's spectrum but stepped its band edge at 0.7 rad (NR, 4.5 MHz off)
    /// and 2 rad (DBPSK, 9 MHz off), and they read 1.11 % and 0.83 % against 0.38 % and 0.006 % centred. Bound: the
    /// excess √(EVM_off² − EVM_centred²) under 0.25 %, a quarter of the old error. The warp's closed form through this
    /// filter is 1.06 % for the NR carrier at the old step (1.05 % read) and 0.18 % at the step the rule now takes.
    /// </summary>
    [Theory]
    [InlineData("NR;BW=5MHz;SCS=15kHz;Mod=64QAM;P=0;Seed=1", "-4.5MHz")]
    [InlineData("DBPSK;Rb=1Mbps;Data=PN9;P=0", "9MHz")]
    public void ACarrierOffTheEnvelopesCentre_ReadsTheEvmItReadsAtTheCentre(string carrier, string off)
    {
        double Evm(string o) => At(Run(Bandpass($"{carrier};Off={o}")).ds, "EVM_pct");
        double centred = Evm("0"), offCentre = Evm(off);
        double excess = Math.Sqrt(Math.Max(0, offCentre * offCentre - centred * centred));
        output.WriteLine($"EVM centred {centred:F4} %, {off} off {offCentre:F4} %, excess {excess:F4} %");
        Assert.InRange(excess, 0, 0.25);
    }

    // An amplifier far below compression (output -37 dBm, IP3 20 dBm) behind a 50 Ω source resistor, noiseless.
    private static string SmallAmp(string source, string stop) => $"""
        temp = 16.85
        {source}
        R:RS s a R=50 Ohm
        Amp:A1 a 0 b 0 Gain=20 IP3=20
        IProbe:Iout b c
        R:RL c 0 R=50 Ohm Noise=no
        analysis ENV1 type=env Accuracy=draft MaxHarm=3 Segments=1 Warmup=1e-6 Stop={stop}
        """;

    /// <summary>
    /// The run's tolerance follows its signal (EnvelopeEngine.RunTolerance). At the default Tol (1e-6 A, ~1 % of this
    /// probe's current) the noiseless run accepted its step predictor about a percent off: EVM 3.463 % where the clipped
    /// record reads 3.228 %, NMSE -32 dB. Solved to 1e-4 of its own excitation it reads the record's EVM and an NMSE far
    /// below any distortion.
    /// </summary>
    [Fact]
    public void ASmallSignal_IsSolvedAsCloselyAsALargeOne()
    {
        var (ds, sig) = Run(SmallAmp($"MOD_Source:S1 s 0 Fc=2e9 Pavg=-10 Z=1e-3 Record=0.5e-3 Clip=8 {Lte}", "0.5e-3"));
        double own = SignalEvm.Measure(sig.Carriers[0].Spec, sig.Carriers[0].Native, sig.RecordSeconds).RmsPercent;
        output.WriteLine($"EVM {At(ds, "EVM_pct"):F4} %, the record's own {own:F4} %, NMSE {At(ds, "NMSE_dB"):F1} dB");
        Assert.Equal(own, At(ds, "EVM_pct"), 0.01);
        Assert.True(At(ds, "NMSE_dB") < -80);
    }

    /// <summary>
    /// A DBPSK record starts on a phase reversal, where its drive is zero: the harmonic-balance start through a nonlinear
    /// block refused the tolerance as one that could not constrain that drive, and the run failed. The start is now its
    /// seed, and the run measures the record's own (zero) EVM.
    /// </summary>
    [Fact]
    public void ARecordStartingAtZeroDrive_StartsThroughANonlinearBlock()
    {
        const string carrier = "DBPSK;Rb=1kbps;Data=0110100111;Off=0;P=0";       // the record's first sample is 0
        string stop = RecordOf(carrier).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var (ds, sig) = Run(SmallAmp($"MOD_Source:S1 s 0 Fc=868e6 Pavg=-10 Z=1e-3 Carrier[1]=\"{carrier}\"", stop));
        Assert.True(sig.Carriers[0].Native[0].Magnitude < 1e-12);
        output.WriteLine($"EVM {At(ds, "EVM_pct"):E3} %");
        Assert.True(At(ds, "EVM_pct") < 0.01);
    }

    /// <summary>
    /// The relative tolerance stops at round-off. A biased stage behind ideal chokes carries a ~3e6 A Norton current at DC
    /// (the 1 µΩ regularisation across 3 V), and its residual cannot fall below a machine epsilon of that; a -100 dBm signal's
    /// 1e-4 lay under it, and every run below ~-75 dBm failed to start. The floor holds, and an excitation large enough is on
    /// Tol exactly, as before.
    /// </summary>
    [Fact]
    public void RunTolerance_StopsAtRoundOffOnTheLargestCurrent()
    {
        const double dc = 3e6;
        System.Numerics.Complex[][] Src(double rf) => [[dc], [rf], [0]];
        double small = CircuitRF.Engine.Envelope.EnvelopeEngine.RunTolerance(1e-6, Src(6e-8), k => k > 0);
        Assert.Equal(CircuitRF.Engine.Envelope.EnvelopeEngine.RoundOffFloor * dc, small, 1e-24);
        double large = CircuitRF.Engine.Envelope.EnvelopeEngine.RunTolerance(1e-6, Src(1e-1), k => k > 0);
        Assert.Equal(1e-6, large);
        double mid = CircuitRF.Engine.Envelope.EnvelopeEngine.RunTolerance(1e-6, Src(1e-3), k => k > 0);
        Assert.Equal(1e-7, mid, 1e-20);
    }
}
