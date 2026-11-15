// brief-paa-4 — the Phased Antenna Array in Feed mode: element scope, the weight functions, quantisation, the channel cell, the System
// chain and the PhaseShifter block, through the schematic path (CircuitEvaluation on .cnl text): gates 1-7. Fixtures: testdata/phased-array
// (5.9 GHz, 5° grid), whose records do not store their ports' positions, so a line on a sub-array states RecordRows/Cols/Dx/Dy.

using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine.AntennaArray;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayFeedTests(ITestOutputHelper output) : IDisposable
{
    private const double F = 5.9e9, Pitch = 25.86e-3, C = 299_792_458.0;
    private static readonly double K = 2 * Math.PI * F / C;
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa4-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static readonly string E5 = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";
    private static readonly string E1 = $"Record=\"{Record("e1")}\" dx=25.86 mm dy=25.86 mm";
    private const string Fields = "analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1\n";
    private const string Port = "Port:P1 f 0 Num=1 Z=50 Ohm\n";

    private RunResult Evaluate(string text, params string[] expressions)
        => CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest { Expressions = expressions });

    private RunResult Run(string text, params string[] expressions)
    {
        var run = Evaluate(text, expressions);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    private static double Value(RunResult run, int k)
        => run.Expressions[k].Value?.AsReal() ?? throw new Xunit.Sdk.XunitException(run.Expressions[k].Error);

    private static DataSet Data(RunResult run, string name = "F1") => run.Results.Single(r => r.Name == name).Data;

    private void Same(DataCube want, DataCube got, double tol, string what)
    {
        var a = want.ComplexValues;
        var b = got.ComplexValues;
        Assert.Equal(a.Length, b.Length);
        double peak = a.Max(c => c.Magnitude), worst = 0;
        for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, (a[i] - b[i]).Magnitude / peak);
        output.WriteLine($"{what}: worst difference {worst:E2} of the peak");
        Assert.True(worst <= tol, $"{what}: worst difference {worst:E2} of the peak");
    }

    /// <summary>Gate 1 — a 16×16 Feed-mode array on the 5×5 record with Phase=steer(theta0, 0, f0) points its beam at θ0 ∈ {0°, 30°, 45°},
    /// and a parametric sweep of theta0 returns one pattern per point. f0 is the record's own 5.9 GHz: steered at 5.8 GHz and read at 5.9 the
    /// beam squints by design, 0.75° at 45°.</summary>
    [Fact]
    public void Steering_PointsTheBeam_AndASweepOfTheta0_GivesAPatternPerPoint()
    {
        string Line(double theta0) => Port + $"theta0 = {theta0} deg\nf0 = 5.9 GHz\n" +
                                      $"PhasedArray:PA1 f {E5} Mode=feed Rows=16 Cols=16 Phase=steer(theta0,0,f0)\n" + Fields;
        foreach (double t in (double[])[0, 30, 45])
        {
            var run = Run(Line(t), $"PointingErrorDeg(F1, {t}, 0)", "HpbwDeg(F1, \"elevation\")");
            output.WriteLine($"θ0 = {t}°: pointing error {Value(run, 0):F4}°, beamwidth {Value(run, 1):F3}°");
            // A tenth of the beam: the embedded pattern tilts the product's peak towards boresight by a fraction of the array's beamwidth.
            Assert.True(Value(run, 0) <= 0.1 * Value(run, 1), $"θ0 = {t}°: pointing error {Value(run, 0)}°");
        }
        var sweep = Run(Line(30) + "analysis SW1 type=parametric_sweep Var=theta0 Values=0,30,45 Unit=deg Inner=F1\n");
        var et = Data(sweep).Cubes["farfield.Etheta"];
        Assert.Equal(3, et.Axes[0].Values.Length);
        int per = et.ComplexValues.Length / 3;
        var points = Enumerable.Range(0, 3).Select(p => et.ComplexValues.Skip(p * per).Take(per).ToArray()).ToArray();
        Assert.True(points[0].Zip(points[2]).Max(z => (z.First - z.Second).Magnitude) > 1e-3 * points[0].Max(c => c.Magnitude), "the points' patterns are one pattern");
    }

    /// <summary>Gate 2 — Phase=steer(…) in element scope and the same phases written as 256 Phase[n] overrides give one pattern to 1e-12; a
    /// VAR reading row is refused, naming where row is valid.</summary>
    [Fact]
    public void ElementScope_EqualsTheOverrides_AndRowOutsideIt_IsRefused()
    {
        string head = Port + "theta0 = 30 deg\nf0 = 5.9 GHz\n";
        var scoped = Run(head + $"PhasedArray:PA1 f {E5} Mode=feed Rows=16 Cols=16 Phase=steer(theta0,0,f0)\n" + Fields);
        var lattice = new ArrayLattice(16, 16, Pitch, Pitch);
        var overrides = string.Join(' ', Enumerable.Range(0, 256).Select(e =>
            $"Phase[{e + 1}]={-360 * 5.9e9 / C * lattice.Position(e).X * Math.Sin(Math.PI / 6):R}"));
        var written = Run(head + $"PhasedArray:PA1 f {E5} Mode=feed Rows=16 Cols=16 {overrides}\n" + Fields);
        Same(Data(scoped).Cubes["farfield.Etheta"], Data(written).Cubes["farfield.Etheta"], 1e-12, "E_theta");
        Same(Data(scoped).Cubes["farfield.Ephi"], Data(written).Cubes["farfield.Ephi"], 1e-12, "E_phi");

        var refused = Evaluate(head + "bad = 10*row\n" + $"PhasedArray:PA1 f {E5} Mode=feed Rows=4 Cols=4 Phase=bad\n" + Fields);
        Assert.NotEqual(RunStatus.Success, refused.Status);
        output.WriteLine(refused.StatusMessage);
        Assert.Contains("row is an element's row, bound only in a Phased Antenna Array's Amp, Phase and Delay", refused.StatusMessage);
    }

    /// <summary>Gate 3 — Amp=taylor(-30,4) on a 32×32 array of isotropic elements: the first sidelobe in both principal planes within 0.5 dB
    /// of −30 dB. The weights are the circuit's (array.wQuantised, from the component on the one-element record); the isotropic element is
    /// the engine's, as PAA-2's local evaluation reads it.</summary>
    [Fact]
    public void Taylor_GivesItsSidelobeLevel_InBothPrincipalPlanes()
    {
        var run = Run(Port + $"PhasedArray:PA1 f {E1} Mode=feed Rows=32 Cols=32 Amp=taylor(-30,4)\n" + Fields, "SllDb(F1)");
        var w = Data(run).Cubes["array.wQuantised"].ComplexValues;
        Assert.Equal(1024, w.Length);
        Assert.Equal(1.0, w.Max(c => c.Magnitude), 12);
        var e1 = CircuitRF.Design.Em3d.PortFarFieldSet.Open(Record("e1")).Set!;
        int nd = e1.Grid.ThetaDeg.Count * e1.Grid.PhiDeg.Count;
        var iso = new ElementPattern(e1.Grid, [.. Enumerable.Repeat(Complex.One, nd)], new Complex[nd]);
        var lattice = new ArrayLattice(32, 32, Pitch, Pitch);
        var beam = ArrayBeam.Evaluate(lattice, w, ArrayElements.Single(iso, 1024), F, 0, 0);
        output.WriteLine($"isotropic: elevation {beam.ElevationSidelobeDb:F3} dB, azimuth {beam.AzimuthSidelobeDb:F3} dB; on the patch element {Value(run, 0):F3} dB");
        Assert.InRange(beam.ElevationSidelobeDb!.Value, -30.5, -29.5);
        Assert.InRange(beam.AzimuthSidelobeDb!.Value, -30.5, -29.5);
        // The circuit's own reading, on the patch element: a boresight beam is cut in the lattice's planes, not along the azimuth its peak
        // happened to land on (a diagonal read −45 dB before ArrayBeam took the reference plane there).
        Assert.InRange(Value(run, 0), -30.5, -29.5);
    }

    /// <summary>Gate 4 — PhaseBits=3 on a 1×16 line steered to 10°: the largest quantisation lobe where the closed form for a 45° step puts
    /// it. Rounding to Δ = 45° leaves a phase error periodic in the phase with period Δ, so e^{jQ(ψ)} = Σ_p c_p·e^{j(1+8p)ψ} with
    /// c_p = sinc(π/8 + pπ): beams at (1 + 8p)·u0 (folded into real space by λ/d), the p = −1 one sinc(7π/8)/sinc(π/8), −16.9 dB. On 16
    /// elements the lobes' own sidelobes overlap, so its peak is held to its main lobe (half a null-to-null width, λ/(N·d)) and its level to
    /// 1 dB; the continuous line has nothing within 4 dB of it there. PhaseBits=0 is the line with no PhaseBits at all.</summary>
    [Fact]
    public void PhaseBits_PutQuantisationLobes_WhereTheClosedFormDoes()
    {
        string Line(string bits) => Port + "theta0 = 10 deg\nf0 = 5.9 GHz\n" +
                                    $"PhasedArray:PA1 f {E1} Mode=feed Rows=1 Cols=16 Phase=steer(theta0,0,f0) {bits}\n" + Fields;
        var lattice = new ArrayLattice(1, 16, Pitch, Pitch);
        Func<double, double> Pattern(Complex[] w) => u =>
        {
            Complex s = 0;
            for (int e = 0; e < 16; e++) s += w[e] * Complex.FromPolarCoordinates(1, K * lattice.Position(e).X * u);
            return 20 * Math.Log10(s.Magnitude);
        };
        var quantised = Pattern(Data(Run(Line("PhaseBits=3"))).Cubes["array.wQuantised"].ComplexValues);
        var continuous = Pattern(Data(Run(Line(""))).Cubes["array.wQuantised"].ComplexValues);
        double u0 = Math.Sin(Math.PI / 18), period = C / F / Pitch, half = period / 16;
        double Wrap(double u) { while (u > 1) u -= period; while (u < -1) u += period; return u; }
        (double U, double Db) Max(Func<double, double> db, double centre)
        {
            (double U, double Db) best = (centre, double.NegativeInfinity);
            for (double u = centre - half; u <= centre + half; u += 1e-5)
                if (Math.Abs(u) <= 1 && db(u) is var d && d > best.Db) best = (u, d);
            return best;
        }
        double peakQ = quantised(u0), peakC = continuous(u0);
        double want = Wrap(-7 * u0);
        double x = Math.PI / 8 - Math.PI, level = 20 * Math.Log10(Math.Abs(Math.Sin(x) / x) / (Math.Sin(Math.PI / 8) / (Math.PI / 8)));
        var lobe = Max(quantised, want);
        var none = Max(continuous, want);
        output.WriteLine($"p = -1: lobe at u = {lobe.U:F4} (closed form {want:F4}, its main lobe ±{half:F4}), {lobe.Db - peakQ:F2} dB " +
                         $"(closed form {level:F2} dB); the continuous line {none.Db - peakC:F2} dB there");
        Assert.True(Math.Abs(lobe.U - want) < half, $"the lobe is at u = {lobe.U}, outside the closed form's at {want}");
        Assert.True(Math.Abs(lobe.Db - peakQ - level) <= 1, $"{lobe.Db - peakQ} dB against {level} dB");
        Assert.True(lobe.Db - peakQ >= none.Db - peakC + 4, "the continuous line has as much there");

        var a = Data(Run(Line(""))).Cubes["farfield.Etheta"].ComplexValues;
        var b = Data(Run(Line("PhaseBits=0"))).Cubes["farfield.Etheta"].ComplexValues;
        Assert.Equal(a, b);
    }

    private const string Channel = """
        define chan ( in out )
        Amp:A1 in 0 mid 0 Gain=20 IP3=200
        Atten:T1 mid 0 out 0 Loss=LossDb
        end
        """;

    /// <summary>Gate 5 — a channel of an Amplifier (20 dB) and an Attenuator (3 dB) lifts the realized gain 17 dB above the same array with
    /// no channel, to 1e-9 dB, and is identical across elements; one whose attenuator is 3 + 0.1·n dB is not.</summary>
    [Fact]
    public void AChannel_AddsItsGain_AndIsIdenticalUnlessItReadsTheElement()
    {
        string Line(string extra, string loss = "3") => Channel.Replace("LossDb", loss) + "\n" + Port + "theta0 = 20 deg\nf0 = 5.9 GHz\n" +
                                                        $"PhasedArray:PA1 f {E5} Mode=feed Rows=4 Cols=4 Phase=steer(theta0,0,f0) {extra}\n" + Fields;
        var bare = Run(Line(""));
        var with = Run(Line("Channel=chan"), "V(\"PA1.ch[3].out\")");
        double g0 = Data(bare).Cubes["farfield.RealizedGainDbi"].RealValues[0], g1 = Data(with).Cubes["farfield.RealizedGainDbi"].RealValues[0];
        output.WriteLine($"realized gain {g0:F6} dBi bare, {g1:F6} dBi with the channel: {g1 - g0:F12} dB");
        Assert.True(Math.Abs(g1 - g0 - 17) <= 1e-9, $"{g1 - g0} dB");
        Assert.Contains(with.Notes, s => s.Contains("is identical across elements", StringComparison.Ordinal));

        var differs = Run(Line("Channel=chan", "3+0.1*n"));
        var note = differs.Notes.Single(s => s.Contains("places channel cell 'chan'", StringComparison.Ordinal));
        output.WriteLine(note);
        Assert.Contains("different per element (it reads n)", note);
    }

    /// <summary>R-paa4-5 / R-paa4-6 — explain says which channel is identical, and that an identical channel of an element switched off is
    /// omitted; check states Feed mode's beamformer. The CLI as a process, as a user runs it.</summary>
    [Fact]
    public void ExplainAndCheck_SayWhatFeedModePlaced()
    {
        string cnl = Path.Combine(_dir, "feed.cnl");
        File.WriteAllText(cnl, Channel.Replace("LossDb", "3") + "\n" + Port + "theta0 = 20 deg\nf0 = 5.9 GHz\n" +
                               $"PhasedArray:PA1 f {E5} Mode=feed Rows=4 Cols=4 Phase=steer(theta0,0,f0) PhaseBits=4 Channel=chan Off=6\n" + Fields);
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "circuitRF.slnx"))) root = Path.GetDirectoryName(root)!;
        var (code, stdout, stderr) = CircuitRF.Ui.Tests.Em3d.CliProcess.Run(root, [], "explain", cnl, "--analysis", "F1");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, code);
        Assert.Contains("is identical across elements", stdout);
        Assert.Contains("element 6, switched off, is omitted", stdout);
        Assert.Contains("rounded to 22.5° (4 bits)", stdout);
        var check = CircuitRF.Ui.Tests.Em3d.CliProcess.Run(root, [], "check", cnl);
        output.WriteLine(check.StdOut + check.StdErr);
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("Feed mode: one pin, F, split 16 ways", check.StdOut + check.StdErr);
    }

    /// <summary>Gate 6 — Port → Mixer → Filter → a Feed-mode array with an Amplifier channel runs S-parameters (with noise) and HB; and the
    /// noise figure from the Port to the beam's peak is the cascade's closed form, to 1e-6 dB, with Coupling=off. The small-signal noise
    /// figure is S-parameter noise, which a mixer stays out of (no conversion path without an LO, MixerModel), so the closed-form comparison is
    /// the chain from the mixer's output on: a common amplifier, a matched loss for the filter's, and the array.</summary>
    [Fact]
    public void ASystemChain_Runs_AndTheBeamNoiseFigure_IsTheCascades()
    {
        const string channel = """
            define ampch ( in out )
            Amp:A1 in 0 out 0 Gain=12 IP3=200 NF=3
            end
            """;
        string Chain(string source, string analysis) => channel + "\n" + source + "\n" +
            "P1Tone:Plo lo 0 Num=2 Pavl=7 Z=50 Freq=5.5e9\n" +
            "Mixer:X1 ifin 0 lo 0 rf 0 ConvGain=-7 Plo=7\n" +
            "Filter:BP rf 0 f 0 Response=Butterworth Form=Bandpass Order=3 F1=5.6 GHz F2=6.2 GHz IL=0.5\n" +
            $"PhasedArray:PA1 f {E1} Mode=feed Rows=2 Cols=2 Channel=ampch Coupling=off\n" + analysis + "\n";
        var hb = Run(Chain("P1Tone:Pif ifin 0 Pavl=-30 Z=50 Freq=0.4e9",
                           "analysis HB1 type=hb NumFreqs=2 Tone[1]=0.4e9 Tone[2]=5.5e9 MaxMixOrder=3 MaxHarm=3 Tol=1e-9"));
        Assert.True(Data(hb, "HB1").Cubes.ContainsKey("V"));
        var sp = Run(Chain("Port:P1 ifin 0 Num=1 Z=50 Ohm", "analysis SP1 type=sparam start=5.8 stop=6 npts=3 Unit=GHz Noise=yes"));
        Assert.True(Data(sp, "SP1").Cubes.ContainsKey("Cs"));
        // …and in an envelope run: a CW carrier at the array's frequency through an IProbe into the feed.
        var env = Run(channel + "\n" + "MOD_Source:S1 s 0 Fc=5.9e9 Pavg=-10 Z=50 Record=0.2e-3 Carrier[1]=\"CW;Off=0;P=0\"\n" +
                      "IProbe:Iin s f\n" + $"PhasedArray:PA1 f {E1} Mode=feed Rows=2 Cols=2 Channel=ampch\n" +
                      "analysis ENV1 type=env Accuracy=draft MaxHarm=3 Segments=1 Warmup=0 Probes=\"s,f\"\n");
        Assert.NotEmpty(Data(env, "ENV1").Cubes);

        // The cascade at T0 (temp = 16.85 °C): amplifier 15 dB / 2 dB, a matched 2 dB loss, then the array's amplifier channel 12 dB / 3 dB.
        string rf = "temp = 16.85\n" + channel + "\n" + Port.Replace(" f ", " a ") +
                    "Amp:A0 a 0 b 0 Gain=15 IP3=200 NF=2\n" +
                    "Atten:L0 b 0 f 0 Loss=2\n" +
                    $"PhasedArray:PA1 f {E1} Mode=feed Rows=4 Cols=1 Channel=ampch Coupling=off\n" + Fields;
        // A column: the element's own peak sits 0.16° off boresight across its H-plane (x), where a column's elements are all in phase, so
        // the split's isolation noise cancels at the peak as the closed form assumes. Along x the same array reads 6e-6 dB higher — the
        // noise the split radiates at a peak its weights are not quite phased to, which is the beam's, not an error.
        var run = Run(rf, "BeamNfDb(F1)");
        double F1 = Math.Pow(10, 0.2), G1 = Math.Pow(10, 1.5), L = Math.Pow(10, 0.2), F2 = Math.Pow(10, 0.3);
        double want = 10 * Math.Log10(F1 + (L - 1) / G1 + (F2 - 1) * L / G1);
        output.WriteLine($"beam noise figure {Value(run, 0):F9} dB, cascade {want:F9} dB");
        Assert.True(Math.Abs(Value(run, 0) - want) <= 1e-6, $"{Value(run, 0)} dB against {want} dB");
    }

    /// <summary>Gate 7 — the PhaseShifter: Phase=90 is S21 = −j at IL=0, lossless and reciprocal; Bits=2 rounds 100° to 90°.</summary>
    [Fact]
    public void PhaseShifter_TurnsThePhase_AndRoundsToItsBits()
    {
        Complex S21(string pars)
        {
            var run = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\n" + $"PhaseShifter:PS1 a 0 b 0 {pars}\n" +
                          "analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz\n");
            var s = Data(run, "SP1").Cubes["S"].ComplexValues;   // [freq, i, j]
            Assert.Equal(s[1], s[2]);                              // reciprocal
            Assert.Equal(0, s[0].Magnitude, 15);                  // matched
            return s[2];
        }
        var q = S21("Phase=90 IL=0");
        output.WriteLine($"Phase=90: S21 = {q}");
        Assert.True((q - new Complex(0, -1)).Magnitude <= 1e-12, $"S21 = {q}");
        Assert.True((S21("Phase=100 Bits=2") - new Complex(0, -1)).Magnitude <= 1e-12);
        Assert.True((S21("Phase=90 deg") - new Complex(0, -1)).Magnitude <= 1e-12);
    }
}
