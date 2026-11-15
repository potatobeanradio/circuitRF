// brief-paa-6b — brute-force envelope: a fields analysis whose waves come from an envelope analysis (waves=ENV1) gives the modulated signal
// each named direction receives, each element's active reflection over the record and a time-averaged pattern. Gates 1-5 on the shipped
// bench testdata/phased-array/hb-1x4/Array Envelope.cnl (one MOD_Source through a 4-way Combiner into the PAA-6 amplifiers on PAA-1's
// E3 record), with an NR 10 MHz 60 kHz carrier and a 40 us record: the shortest that holds a whole symbol after the settle at this
// numerology, so EVM is formed and each run stays near the routine budget.

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Circuit;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayEnvelopeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa6b-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Fixture(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", name));

    private const string TestCarrier = "NR;BW=10MHz;SCS=60kHz;Mod=64QAM;Off=0;P=0;Seed=1";
    private const string Env = "analysis ENV1 type=env Stop=40e-6 Step=auto Accuracy=standard MaxHarm=5 Segments=auto Warmup=auto Start=0 Tol=1e-6 SaveWaveforms=no\n";

    /// <summary>The shipped envelope bench's circuit, its record path absolute, its analyses left out, at <paramref name="pavgDbm"/> and
    /// <paramref name="theta0Deg"/>, with <paramref name="carrier"/>, then <paramref name="tail"/>.</summary>
    private static string Bench(string tail, double pavgDbm, double theta0Deg, string carrier = TestCarrier, string combiner = "")
    {
        string text = File.ReadAllText(Fixture(Path.Combine("hb-1x4", "Array Envelope.cnl")));
        text = text.Replace("File=\"../results/e3x.cemr\"", $"File=\"{Fixture(Path.Combine("results", "e3x.cemr"))}\"");
        var lines = text.Split('\n').Where(l => !l.StartsWith("analysis ", StringComparison.Ordinal) && !l.StartsWith("measure ", StringComparison.Ordinal))
                        .Select(l => l.StartsWith("Pavg = ", StringComparison.Ordinal) ? $"Pavg = {pavgDbm.ToString(CultureInfo.InvariantCulture)}"
                                   : l.StartsWith("theta0 = ", StringComparison.Ordinal) ? $"theta0 = {theta0Deg.ToString(CultureInfo.InvariantCulture)} deg"
                                   : l.StartsWith("MOD_Source:SRC1", StringComparison.Ordinal) ? System.Text.RegularExpressions.Regex.Replace(l, "Carrier\\[1\\]=\"[^\"]*\"", $"Carrier[1]=\"{carrier}\"")
                                   : l.StartsWith("Combiner:SPL", StringComparison.Ordinal) ? l + combiner : l);
        return string.Join("\n", lines) + "\n" + tail;
    }

    private RunResult Run(string text)
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest());
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    private static DataSet Data(RunResult run, string name) => run.Results.Single(r => r.Name == name).Data;

    private static int Label(DataCube cube, int axis, string label)
    {
        int at = Array.IndexOf(cube.Axes[axis].Labels!, label);
        Assert.True(at >= 0, $"{cube.Axes[axis].Name} has no {label}: {string.Join(", ", cube.Axes[axis].Labels!)}");
        return at;
    }

    /// <summary>ACLR's ACPR1 and ACPR2, lower and upper (dBc), of one branch or direction; and its EVM (%).</summary>
    private static (double[] Aclr, double Evm) Quality(DataSet d, string prefix, string label)
    {
        var aclr = d.Cubes[$"{prefix}ACLR"];
        int b = Label(aclr, 0, label), per = aclr.RealValues.Length / aclr.Axes[0].Length;
        var evm = d.Cubes[$"{prefix}EVM_pct"];
        return ([.. aclr.RealValues.Skip(b * per + 1).Take(4)], evm.RealValues[Label(evm, 0, label) * (evm.RealValues.Length / evm.Axes[0].Length)]);
    }

    /// <summary>Gate 1 — an unmodulated carrier through the envelope is harmonic balance: at a compressed drive the radiated field in every named
    /// direction is PAA-6's waves=HB1 pattern there (the same bench with the source a P1Tone of the same available power), up to the carrier's
    /// common phase, and every element's active reflection is HB's, to the envelope's Newton tolerance; and dirs=peak lands on HB's beam.
    /// Measured: the shape 9.9e-9 of the peak and the reflections 3.8e-9 apart, the level 4.4e-6 (a MOD_Source's CW against a P1Tone).</summary>
    [Fact]
    public void ACwEnvelope_IsHarmonicBalance()
    {
        const string Dirs = "dirs=\"peak;0,0;-30,0;60,0\"";
        var env = Data(Run(Bench("analysis ENV1 type=env Stop=4e-6 Step=20e-9 MaxHarm=5 Segments=1 Warmup=0 Tol=1e-7 SaveWaveforms=no\n" +
                                 $"analysis F1 type=fields block=EM1 waves=ENV1 {Dirs}\n", 22, 30, "CW;Off=0;P=0")), "F1");
        string hbText = Bench("analysis HB1 type=hb Tone=\"f0\" ToneUnit=GHz MaxHarm=5 FFTOverSample=1 Tol=1e-10\nanalysis F1 type=fields block=EM1 waves=HB1\n", 22, 30)
                        .Replace("MOD_Source:SRC1  s  0  Fc=f0  Pavg=Pavg dBm  Z=50 Ohm", "P1Tone:SRC1  s  0  Num=1  Pavl=Pavg dBm  Z=50 Ohm  Freq=f0  Phase=0 deg ;");
        var hb = Data(Run(hbText), "F1");

        var et = env.Cubes["radiated.Etheta"];
        var ep = env.Cubes["radiated.Ephi"];
        int nt = et.Axes[0].Length, nn = et.Axes[1].Length;
        var th = env.Cubes["radiated.theta"].RealValues;
        var ph = env.Cubes["radiated.phi"].RealValues;
        var ht = hb.Cubes["farfield.Etheta"];
        var hp = hb.Cubes["farfield.Ephi"];
        var thetaAxis = ht.Axes[1].Values;
        var phiAxis = ht.Axes[2].Values;
        int At(double t, double p) => Array.IndexOf(thetaAxis, t) * phiAxis.Length + Array.IndexOf(phiAxis, p);
        var x = new List<Complex>();
        var y = new List<Complex>();
        for (int d = 0; d < nn; d++)
        {
            x.Add(et.ComplexValues[(nt - 1) * nn + d]); x.Add(ep.ComplexValues[(nt - 1) * nn + d]);
            y.Add(ht.ComplexValues[At(th[d], ph[d])]); y.Add(hp.ComplexValues[At(th[d], ph[d])]);
        }
        Complex num = 0;
        double den = 0, peak = y.Max(c => c.Magnitude), worst = 0;
        for (int i = 0; i < x.Count; i++) { num += Complex.Conjugate(y[i]) * x[i]; den += y[i].Magnitude * y[i].Magnitude; }
        Complex r = num / den;
        for (int i = 0; i < x.Count; i++) worst = Math.Max(worst, (x[i] - r * y[i]).Magnitude / peak);
        var ge = env.Cubes["array.GammaActive"].ComplexValues;
        var gh = hb.Cubes["array.GammaActive"].ComplexValues;
        double worstGamma = Enumerable.Range(0, 4).Max(e => (ge[(nt - 1) * 4 + e] - gh[e]).Magnitude);
        output.WriteLine($"field: |common factor| {r.Magnitude:F9}, worst residual {worst:E2} of the peak; active reflection worst {worstGamma:E2}; " +
                         $"peak θ {th[0]}° φ {ph[0]}° against HB's {hb.Cubes["farfield.DirectivityPeakThetaDeg"].RealValues[0]}°");
        // one level for every direction and polarization at once: the two sources' levels, 4.4e-6 apart measured, while the shape is 1e-8
        Assert.True(Math.Abs(r.Magnitude - 1) <= 1e-5, $"the envelope's field is {r.Magnitude} of HB's");
        Assert.True(worst <= 1e-6, $"the field differs by {worst:E2} of its peak after the carrier's phase");
        Assert.True(worstGamma <= 1e-6, $"the active reflection differs by {worstGamma:E2}");
        Assert.True(Math.Abs(th[0] - hb.Cubes["farfield.DirectivityPeakThetaDeg"].RealValues[0]) <= 5, "dirs=peak is not HB's beam");
    }

    /// <summary>Gate 2 — with three of the four channels off (200 dB on their Combiner paths), every direction's radiated envelope is the driven PA's
    /// output envelope times one complex factor, so its ACLR and EVM are the ones the envelope reports at that PA's output IProbe: 0.01 dB, 0.01 pp.
    /// At a drive that compresses the PA, so the ACLR is its own distortion.</summary>
    [Fact]
    public void OneElementAlone_RadiatesThatAmplifiersOwnSignal()
    {
        var run = Run(Bench(Env + "analysis F1 type=fields block=EM1 waves=ENV1 dirs=\"peak;0,0;-30,0;60,0\"\n", 22, 0, combiner: " IL[2]=200 dB IL[3]=200 dB IL[4]=200 dB"));
        var (aclrPa, evmPa) = Quality(Data(run, "ENV1"), "", "PA1.Iout");
        var f1 = Data(run, "F1");
        foreach (string dir in f1.Cubes["radiated.ACLR"].Axes[0].Labels!)
        {
            var (aclr, evm) = Quality(f1, "radiated.", dir);
            output.WriteLine($"{dir}: ACLR {string.Join(" ", aclr.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))} dBc, EVM {evm:F4} % " +
                             $"(PA1 {string.Join(" ", aclrPa.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))}, {evmPa:F4} %)");
            for (int m = 0; m < 2; m++) Assert.True(Math.Abs(aclr[m] - aclrPa[m]) <= 0.01, $"{dir}: ACPR1 differs by {aclr[m] - aclrPa[m]:F4} dB");
            Assert.True(Math.Abs(evm - evmPa) <= 0.01, $"{dir}: EVM differs by {evm - evmPa:F4} pp");
        }
    }

    /// <summary>Gate 3 — far below compression the beam carries the source's signal through a linear chain: its EVM is the source's to 0.1 pp, and its
    /// ACLR is what the amplifiers deliver (PA1's output IProbe) to 0.05 dB. At -38 dBm, 60 dB below compression: this amplifier, biased near
    /// pinch-off, still adds its own distortion above the source's -90 dBc at 30 dB (the beam reads -67.5 dBc at -8 dBm), and the chain's own
    /// frequency response across the band moves ACPR1 0.6 dB from the source's ideal at any drive, which is linear and the beam inherits.</summary>
    [Fact]
    public void BackedOff_TheBeamCarriesTheSourcesSignal()
    {
        var run = Run(Bench(Env + "analysis F1 type=fields block=EM1 waves=ENV1 dirs=peak\n", -38, 30));
        var (aclrSrc, evmSrc) = Quality(Data(run, "ENV1"), "", "Isrc");
        var (aclrPa, _) = Quality(Data(run, "ENV1"), "", "PA1.Iout");
        var (aclr, evm) = Quality(Data(run, "F1"), "radiated.", "peak");
        output.WriteLine($"beam: ACPR1 {aclr[0]:F2}/{aclr[1]:F2} dBc, EVM {evm:F3} %; PA1 out {aclrPa[0]:F2}/{aclrPa[1]:F2} dBc; " +
                         $"source {aclrSrc[0]:F2}/{aclrSrc[1]:F2} dBc, {evmSrc:F3} %");
        Assert.True(Math.Abs(evm - evmSrc) <= 0.1, $"EVM in the beam {evm} %, the source's {evmSrc} %");
        for (int m = 0; m < 2; m++) Assert.True(Math.Abs(aclr[m] - aclrPa[m]) <= 0.05, $"ACPR1 in the beam {aclr[m]}, the amplifier's {aclrPa[m]}");
    }

    /// <summary>Gate 4 — the time-averaged pattern is the average: at three grid directions farfield.U is the mean over the measured window of
    /// (|E_θ|² + |E_φ|²)/2η₀ formed from each step's waves, to 1e-12.</summary>
    [Fact]
    public void TheAveragedPattern_IsTheMeanOfEachStepsField()
    {
        var f1 = Data(Run(Bench(Env.Replace("Stop=40e-6", "Stop=10e-6") + "analysis F1 type=fields block=EM1 waves=ENV1 dirs=\"0,0;-30,0;60,0\"\n", 22, 30)), "F1");
        var et = f1.Cubes["radiated.Etheta"];
        var ep = f1.Cubes["radiated.Ephi"];
        var time = et.Axes[0].Values;
        int nn = et.Axes[1].Length;
        double from = f1.Cubes["radiated.MeasureFrom_s"].RealValues[0];
        int t0 = Array.FindIndex(time, t => t >= from - 1e-15);
        var u = f1.Cubes["farfield.U"];
        var thetaAxis = u.Axes[1].Values;
        var phiAxis = u.Axes[2].Values;
        var th = f1.Cubes["radiated.theta"].RealValues;
        var ph = f1.Cubes["radiated.phi"].RealValues;
        double eta = CircuitRF.Engine.Mom.FarFieldStage.Eta0;
        for (int d = 0; d < nn; d++)
        {
            double sum = 0;
            for (int t = t0; t < time.Length; t++)
                sum += et.ComplexValues[t * nn + d].Magnitude * et.ComplexValues[t * nn + d].Magnitude + ep.ComplexValues[t * nn + d].Magnitude * ep.ComplexValues[t * nn + d].Magnitude;
            double mean = sum / (time.Length - t0) / (2 * eta);
            double avg = u.RealValues[Array.IndexOf(thetaAxis, th[d]) * phiAxis.Length + Array.IndexOf(phiAxis, ph[d])];
            output.WriteLine($"θ {th[d]}° φ {ph[d]}°: U {avg:E6}, mean {mean:E6}, apart {Math.Abs(avg - mean) / mean:E2}");
            Assert.True(Math.Abs(avg - mean) <= 1e-12 * mean, $"θ {th[d]}° φ {ph[d]}°: {avg} against {mean}");
        }
    }

    /// <summary>Gate 5 — directions: seventeen are refused with the count, and a direction between an EM Field Data block's grid points is refused naming
    /// the nearest grid direction, both before the envelope runs.</summary>
    [Fact]
    public void Directions_TooManyOrOffTheGrid_AreRefusedBeforeTheRun()
    {
        string seventeen = string.Join(";", Enumerable.Range(0, 17).Select(i => $"{5 * (i % 10)},{90 * (i / 10)}"));
        string Said(string dirs)
        {
            var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(Bench(Env + $"analysis F1 type=fields block=EM1 waves=ENV1 dirs=\"{dirs}\"\n", 0, 0), _dir, _dir),
                                                 new CircuitEvaluationRequest { Analyses = ["F1"] });
            string s = run.StatusMessage + " " + string.Join(" ", run.Convergence.Select(c => c.Detail));
            output.WriteLine(s);
            return s;
        }
        Assert.Contains("names 17 directions, and a run names at most 16", Said(seventeen));
        Assert.Contains("The nearest is θ 30°, φ 0°", Said("32,1"));
    }
}
