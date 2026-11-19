// brief-paa-7 — the Phased Antenna Array as a receiver: incident waves by reciprocity, the array's own noise through its patterns, and the
// receive metrics, through the schematic path (CircuitEvaluation on .cnl text): gates 1-6. Fixtures: testdata/phased-array (5.9 GHz, 5° grid;
// its README), whose records do not store their ports' positions, so a line on a sub-array states RecordRows/Cols/Dx/Dy. The default ambient
// is 300 K, which is therefore the arrays' physical temperature here.

using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Design.Circuit;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayReceiveTests(ITestOutputHelper output) : IDisposable
{
    private const double F = 5.9e9;
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa7-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static readonly string E5 = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";
    private static readonly string E1 = $"Record=\"{Record("e1")}\" dx=25.86 mm dy=25.86 mm";
    private const string Fields = "analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1\n";

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

    private static DataSet Data(RunResult run, string name) => run.Results.Single(r => r.Name == name).Data;

    private static string Ports(int n) => string.Join("\n", Enumerable.Range(1, n).Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm")) + "\n";

    private static string Nets(int n) => string.Join(' ', Enumerable.Range(1, n).Select(k => $"p{k}"));

    /// <summary>The power an incident wave delivers into a matched 50 Ω load at the feed, from a linear harmonic-balance run.</summary>
    private double Received(string array, string wave)
    {
        var run = Run(Lna + "\nR:RL f 0 R=50 Ohm\n" + $"PhasedArray:PA1 f {array} Mode=feed {wave}\n" +
                      "analysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-12\n", "mag(HB1.V(\"f\",1))");
        double v = Value(run, 0);
        return v * v / (2 * 50);
    }

    /// <summary>Gate 1 — reciprocity at boresight: the power a plane wave of density S delivers into a matched load at the feed, summed over two
    /// orthogonal polarizations, equals S·λ²·G_r/4π with G_r the realized gain of the same array's transmit pattern there, to 1e-9.</summary>
    [Fact]
    public void AtBoresight_TheReceivedPower_IsSLambdaSquaredGrOver4Pi()
    {
        string array = $"{E1} Rows=4 Cols=4 Amp=taylor(-20,3)";
        const double pd = -50;
        string Wave(string pol) => $"RxTheta[1]=0 RxPhi[1]=0 RxPd[1]={pd} RxFreq[1]=5.9 GHz RxPol[1]={pol}";
        double pt = Received(array, Wave("theta")), pp = Received(array, Wave("phi"));
        double gr = Value(Run("Port:P1 f 0 Num=1 Z=50 Ohm\n" + $"PhasedArray:PA1 f {array} Mode=feed\n" + Fields, "RealizedGainDbi(F1, 0, 0)"), 0);
        double s = Math.Pow(10, (pd - 30) / 10), lambda = CircuitRF.Engine.Mom.EmConstants.C0 / F;
        double want = s * lambda * lambda * Math.Pow(10, gr / 10) / (4 * Math.PI);
        double rel = Math.Abs(pt + pp - want) / want;
        output.WriteLine($"G_r {gr:F6} dBi; received {pt:E6} W (θ) + {pp:E6} W (φ) = {pt + pp:E9} W against S·λ²·G_r/4π = {want:E9} W ({rel:E2})");
        Assert.True(rel <= 1e-9, $"relative {rel:E2}");
    }

    /// <summary>
    /// Brief PCH-13 gate 2 — a receiving array's fields pattern is its receive pattern: each element's transfer to the port. On a reciprocal
    /// array it is the transmit pattern, to 1e-9 dB; behind one-way LNAs, where a wave sent in from the port reaches no element, it is the
    /// passive array's raised by the LNAs' 20 dB, at boresight and in a sidelobe direction.
    /// </summary>
    [Fact]
    public void TheReceivePattern_IsTheTransmitOne_OnAReciprocalArray_AndBehindLnasIsRaisedByTheirGain()
    {
        string array = $"{E1} Rows=4 Cols=4 Amp=taylor(-20,3)";
        const string wave = " RxTheta[1]=0 RxPhi[1]=0 RxPd[1]=-60 RxFreq[1]=5.9 GHz RxPol[1]=theta";
        string[] at = ["RealizedGainDbi(F1, 0, 0)", "RealizedGainDbi(F1, 30, 0)"];
        double[] Gains(string extra) => [.. Enumerable.Range(0, at.Length).Select(k => Value(
            Run(Lna + "\nPort:P1 f 0 Num=1 Z=50 Ohm\n" + $"PhasedArray:PA1 f {array}{extra} Mode=feed\n" + Fields, at), k))];
        var transmit = Gains("");
        var receive = Gains(wave);
        var behindLnas = Gains(" Channel=lna" + wave);
        for (int k = 0; k < at.Length; k++)
        {
            output.WriteLine($"{at[k]}: transmit {transmit[k]:F9}, receive {receive[k]:F9}, behind LNAs {behindLnas[k]:F9} dBi");
            Assert.True(Math.Abs(receive[k] - transmit[k]) <= 1e-9, $"{at[k]}: receive {receive[k]} vs transmit {transmit[k]}");
            Assert.True(Math.Abs(behindLnas[k] - (receive[k] + 20)) <= 0.05, $"{at[k]}: behind LNAs {behindLnas[k]} vs {receive[k] + 20}");
        }
    }

    /// <summary>A receive channel: an amplifier from the element (its second port) towards the beamformer (its first).</summary>
    private const string Lna = """
        define lna ( bf el )
        Amp:A1 el 0 bf 0 Gain=20 NF=2
        end
        """;

    /// <summary>Gate 4 — spatial filtering: an interferer arriving at a sidelobe reaches the feed below the same wave at boresight by exactly the
    /// pattern's ratio there, to 1e-6 dB, through linear 20 dB channels (an 8×8 Taylor −25 dB array, isolated elements).</summary>
    [Fact]
    public void AnInterferer_InASidelobe_IsAttenuatedByThePatternsRatio()
    {
        string array = $"{E1} Rows=8 Cols=8 Amp=taylor(-25,4)";
        double[] thetas = [.. Enumerable.Range(0, 18).Select(k => 5.0 * k)];
        var pattern = Run("Port:P1 f 0 Num=1 Z=50 Ohm\n" + $"PhasedArray:PA1 f {array} Mode=feed\n" + Fields,
                          [.. thetas.Select(t => $"RealizedGainDbi(F1, {t}, 0)")]);
        var level = thetas.Select((t, k) => Value(pattern, k) - Value(pattern, 0)).ToArray();
        // past the first null, the sample nearest −25 dB
        int null1 = Enumerable.Range(1, thetas.Length - 2).First(k => level[k] < level[k - 1] && level[k] < level[k + 1]);
        int side = Enumerable.Range(null1 + 1, thetas.Length - null1 - 1).MinBy(k => Math.Abs(level[k] + 25));
        double Power(double theta) => new[] { "theta", "phi" }.Sum(pol =>
            Received(array + " Channel=lna", $"RxTheta[1]={theta} RxPhi[1]=0 RxPd[1]=-60 RxFreq[1]=5.9 GHz RxPol[1]={pol}"));
        double ratio = 10 * Math.Log10(Power(thetas[side]) / Power(0));
        output.WriteLine($"sidelobe sample θ = {thetas[side]}°: pattern {level[side]:F9} dB, received {ratio:F9} dB ({ratio - level[side]:E2} dB)");
        Assert.True(Math.Abs(ratio - level[side]) <= 1e-6, $"{ratio - level[side]} dB");
        Assert.InRange(level[side], -40, -18);
    }

    /// <summary>
    /// A LOSSLESS one-element record written for the test: a terminated (3D-style) result on the whole sphere, a z-directed dipole's pattern
    /// E_θ = A·sinθ per unit incident wave, and S₁₁ = 0.2 with A chosen so the pattern radiates exactly what the port accepts, 1 − |S₁₁|², by the
    /// radiated-power quadrature the code itself integrates with. An EM result of real materials is never this, so the gate needs its own.
    /// </summary>
    private string LosslessRecord()
    {
        const double s11 = 0.2, z0 = 50;
        double[] theta = [.. Enumerable.Range(0, 37).Select(k => 5.0 * k)], phi = [.. Enumerable.Range(0, 72).Select(k => 5.0 * k)];
        var grid = new CircuitRF.Engine.Mom.PlanarFarFieldGrid(theta, phi);
        var w = CircuitRF.Engine.Mom.PlanarFarFieldPattern.SolidAngles(grid);
        double integral = 0;
        for (int t = 0; t < theta.Length; t++)
            for (int p = 0; p < phi.Length; p++) integral += w[grid.IndexOf(t, p)] * Math.Pow(Math.Sin(theta[t] * Math.PI / 180), 2);
        double eta = CircuitRF.Engine.AntennaArray.ArrayWaves.Eta0;
        double amplitude = Math.Sqrt((1 - s11 * s11) * eta / integral);
        var et = new Complex[grid.DirectionCount];
        for (int t = 0; t < theta.Length; t++)
            for (int p = 0; p < phi.Length; p++)
                et[grid.IndexOf(t, p)] = amplitude * Math.Sin(theta[t] * Math.PI / 180) / (Math.Sqrt(z0) * (1 + s11));   // stored per volt
        var ds = new DataSet();
        Axis[] axes = [new("freq", [F], "Hz"), new("theta", theta, "deg"), new("phi", phi, "deg"), new("port", [1.0], "")];
        ds.AddToGroup("farfield", "Etheta", new DataCube(axes, et));
        ds.AddToGroup("farfield", "Ephi", new DataCube(axes, new Complex[grid.DirectionCount]));
        ds.AddToGroup("em", "S", new DataCube([new("freq", [F], "Hz"), new("i", [1.0], "port"), new("j", [1.0], "port")], new Complex[] { s11 }));
        ds.AddToGroup("em", "Z0", new DataCube([new("port", [1.0], "")], new[] { z0 }));
        using (var file = File.Create(Path.Combine(_dir, "lossless_em.npy"))) RfCore.Export.DataSetExporter.ExportNpy(ds, file);
        File.WriteAllText(Path.Combine(_dir, "lossless.s1p"), $"# Hz S RI R 50\n{F} {s11} 0\n");
        string record = Path.Combine(_dir, "lossless.cemr");
        File.WriteAllText(record, $$"""
            { "FormatVersion": 1, "Stamp": "test", "Adopted": true, "Solver": { "Name": "Palace" },
              "Files": { "Snp": "lossless.s1p", "Npy": "lossless_em.npy" },
              "Ports": [ { "Number": 1, "Name": "1", "Kind": "Lumped" } ],
              "Frequencies": { "SweepHz": [ {{F}} ], "FarFieldHz": [ {{F}} ] },
              "FarFieldExcitation": "TerminatedZ0" }
            """);
        return record;
    }

    /// <summary>Gate 3 — a cold sky (Tsky = Tground = 0 K) on a lossless record: the antenna temperature is 0 K to 1e-9 K, and the system
    /// temperature is the receiver's alone — the same as with the array's noise switched off.</summary>
    [Fact]
    public void AColdSky_OnALosslessRecord_HasNoAntennaTemperature()
    {
        string record = LosslessRecord();
        string Bench(string extra) => "Port:P1 out 0 Num=1 Z=50 Ohm\nAmp:A1 p1 0 out 0 Gain=20 NF=2\n" +
                                      $"PhasedArray:PA1 p1 Record=\"{record}\" Rows=1 Cols=1 {extra}\n" +
                                      "analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1\n" +
                                      "analysis NF1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz Noise=yes\n";
        var cold = Run(Bench("Tsky=0 Tground=0"), "AntennaTempK(F1)", "SystemTempK(F1, NF1)");
        var quiet = Run(Bench("Tsky=0 Tground=0 Noise=no"), "SystemTempK(F1, NF1)");
        var warm = Run(Bench(""), "AntennaTempK(F1)", "SystemTempK(F1, NF1)");
        double ta = Value(cold, 0), ts = Value(cold, 1), tq = Value(quiet, 0);
        output.WriteLine($"cold sky: T_A {ta:E3} K, T_sys {ts:F9} K; array switched off: T_sys {tq:F9} K; at its own 300 K: T_A {Value(warm, 0):F9} K, T_sys {Value(warm, 1):F6} K");
        Assert.True(Math.Abs(ta) <= 1e-9, $"T_A {ta} K");
        Assert.True(Math.Abs(ts - tq) <= 1e-9 * tq, $"{ts} K against {tq} K");
        Assert.True(Math.Abs(Value(warm, 0) - 300) <= 1e-9, "at its own temperature an array's antenna temperature is that temperature");
        Assert.True(Math.Abs(Value(warm, 1) - Value(warm, 0) - tq) <= 1e-9 * tq, "T_sys = T_A + T_rx");
    }

    /// <summary>Gate 5 — coupled noise: four LNAs with a poor input match (3 dB return loss) on a 1×4 array, combined to one port. The noise at
    /// the port with Coupling=on differs from Coupling=off — each LNA's own noise leaves its input and reaches its neighbours through S — and with
    /// coupling on equals an EM Field Data block of the same record with explicit LNAs on its four ports, to 1e-9 dB.</summary>
    [Fact]
    public void CoupledLnas_TheComponentsNoise_IsTheBruteForceCircuits()
    {
        string Bench(string array) => "Port:P1 out 0 Num=1 Z=50 Ohm\nCombiner:SUM out 0 o1 0 o2 0 o3 0 o4 0 N=4\n" +
                                      string.Concat(Enumerable.Range(1, 4).Select(k => $"Amp:A{k} p{k} 0 o{k} 0 Gain=20 NF=1.5 RLin=3\n")) + array + "\n" +
                                      "analysis NF1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz Noise=yes\n";
        string rec = $"Record=\"{Record("e3x")}\"";
        string grid = "Rows=1 Cols=4 dx=25.86 mm dy=25.86 mm RecordRows=1 RecordCols=4 RecordDx=25.86 mm RecordDy=25.86 mm Level=exact";
        double Noise(string array) => Data(Run(Bench(array)), "NF1").Cubes["Cs"].ComplexValues[0].Real;
        double on = Noise($"PhasedArray:PA1 {Nets(4)} {rec} {grid}"), off = Noise($"PhasedArray:PA1 {Nets(4)} {rec} {grid} Coupling=off");
        double block = Noise($"EmFieldData:EM1 {Nets(4)} File=\"{Record("e3x")}\"");
        // the noise figure at the port, against the elements at T0 — the array at its own 300 K stands in a known ratio to it
        double db(double a, double b) => 10 * Math.Log10(a / b);
        output.WriteLine($"noise at the port: coupling on {on:E9} W/Hz, off {off:E9} W/Hz ({db(on, off):F4} dB); EM Field Data block {block:E9} W/Hz ({db(on, block):E2} dB)");
        Assert.True(Math.Abs(db(on, block)) <= 1e-9, $"{db(on, block)} dB");
        Assert.True(Math.Abs(db(on, off)) >= 0.01, $"coupling moved the noise {db(on, off)} dB");
    }

    /// <summary>R-paa7-4 — SnrDb is the power an incident wave delivers to the receive port over the noise delivered there: the signal a linear
    /// harmonic-balance run puts into a 50 Ω load at the feed, through a channel of one-way amplifiers (which a transmit pattern cannot see
    /// through), over the noise analysis's Cs at the same port, to 1e-8 dB (the harmonic-balance solve's own tolerance).</summary>
    [Fact]
    public void TheSnr_IsTheReceivedPowerOverTheDeliveredNoise_ThroughOneWayChannels()
    {
        string array = $"{E5} Rows=4 Cols=4 Amp=taylor(-20,3) Channel=lna {ColdSky}";
        const string wave = "RxTheta[1]=20 RxPhi[1]=10 RxPd[1]=-80 RxFreq[1]=5.9 GHz RxPol[1]=45";
        double received = Received(array, wave);
        var run = Run(Lna + "\nPort:P1 f 0 Num=1 Z=50 Ohm\n" + $"PhasedArray:PA1 f {array} Mode=feed {wave}\n" + Fields +
                      "analysis NF1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz Noise=yes\n",
                      "SnrDb(F1, NF1, 1, 1e6)", "GoverTDbK(F1, NF1)", "AntennaTempK(F1)", "SystemTempK(F1, NF1)", "NullDepthDb(F1, 20, 10)");
        double cs = Data(run, "NF1").Cubes["Cs"].ComplexValues[0].Real;
        double want = 10 * Math.Log10(received / (cs * 1e6));
        output.WriteLine($"received {received:E6} W, noise {cs:E6} W/Hz: SNR {want:F9} dB against SnrDb {Value(run, 0):F9} dB; G/T {Value(run, 1):F4} dB/K, " +
                         $"T_A {Value(run, 2):F3} K, T_sys {Value(run, 3):F3} K, pattern at the wave {Value(run, 4):F3} dB");
        Assert.True(Math.Abs(Value(run, 0) - want) <= 1e-8, $"{Value(run, 0) - want} dB");
        Assert.True(Value(run, 3) > Value(run, 2), "T_sys = T_A + T_rx with a noisy receiver");
    }

    /// <summary>R-paa7-6 — the receive bench's own linear analyses (its harmonic balance is a minute's run, regenerated by hand per the README):
    /// the antenna temperature lies between the sky's and the metal's, the system temperature above it, and the SNR of the boresight signal is
    /// its density over the noise floor that G/T sets, within the beam's pointing and the patch's cross-polar part.</summary>
    [Fact]
    public void TheReceiveBench_ReadsItsReceiveMetrics()
    {
        string bench = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Record("e5"))!, "..", "receive-8x8", "Receive Bench.cnl"))
                           .Replace("\"../results/e5.cemr\"", $"\"{Record("e5")}\"");
        string linear = string.Join("\n", bench.Split('\n').Where(l => !l.StartsWith("analysis HB1", StringComparison.Ordinal) && !l.Contains("HB1.V", StringComparison.Ordinal)));
        var run = Run(linear, "AntennaTempK(F1)", "SystemTempK(F1, NF1)", "GoverTDbK(F1, NF1)", "SnrDb(F1, NF1, 1, 1e6)");
        double ta = Value(run, 0), ts = Value(run, 1), gt = Value(run, 2), snr = Value(run, 3);
        double lambda = CircuitRF.Engine.Mom.EmConstants.C0 / F, s = Math.Pow(10, (-90 - 30) / 10.0);
        double fromGt = 10 * Math.Log10(s * lambda * lambda / (4 * Math.PI * NoiseMetrics.Boltzmann * 1e6)) + gt;
        output.WriteLine($"T_A {ta:F3} K, T_sys {ts:F3} K, G/T {gt:F4} dB/K, SNR in 1 MHz {snr:F4} dB (from G/T {fromGt:F4} dB)");
        Assert.InRange(ta, 5, 300);
        Assert.True(ts > ta);
        Assert.True(Math.Abs(snr - fromGt) <= 0.1, $"{snr} dB against {fromGt} dB");
    }

    /// <summary>The sky and the incident waves are written and read back by the .cnl as they were typed.</summary>
    [Fact]
    public void TheSkyAndTheWaves_SurviveTheCnl()
    {
        string path = Path.Combine(_dir, "rx.cnl");
        string line = $"PhasedArray:PA1 f {E1} Mode=feed Rows=2 Cols=2 {ColdSky} RxTheta[1]=30 RxPhi[1]=90 RxPd[1]=-60 RxFreq[1]=5.9 GHz RxPol[1]=rhcp RxPol[2]=45";
        File.WriteAllText(path, "Port:P1 f 0 Num=1 Z=50 Ohm\n" + line + "\n");
        string once = CircuitRF.Core.Netlist.CnlWriter.Write(CircuitRF.Core.Netlist.CnlReader.ReadFile(path).TestBench);
        File.WriteAllText(path, once);
        string twice = CircuitRF.Core.Netlist.CnlWriter.Write(CircuitRF.Core.Netlist.CnlReader.ReadFile(path).TestBench);
        Assert.Equal(once, twice);
        foreach (string key in (string[])["Tsky=5+280*exp(-el/0.0873)", "Tground=290", "RxTheta[1]=30", "RxPhi[1]=90", "RxPd[1]=-60", "RxPol[1]=rhcp", "RxPol[2]=45"])
            Assert.Contains(key, once);
    }

    private const string ColdSky = "Tsky=5+280*exp(-el/0.0873) Tground=290";

    /// <summary>Gate 6 — by offset at size. On a 64×64 Feed-mode array of the 5×5 record's centre pattern under a sky that falls from 285 K at the
    /// horizon to 5 K overhead, the sky's part by element offset equals the pair-by-pair integral on the 16×16 corner to 1e-9 of the diagonal;
    /// and the whole 4096-element noise analysis runs with no pair integral taken and no dense N×N formed.</summary>
    [Fact]
    public void ByOffset_AtSize_IsThePairByPairIntegral_AndHoldsNoDenseMatrix()
    {
        string line = $"PhasedArray:PA1 f {E5} Patterns=centre Mode=feed Rows=64 Cols=64 Amp=taylor(-30,4) {ColdSky}";
        long pairs = ArrayNoise.PairIntegrals, dense = ArrayCorrelation.DenseCopies, offsets = ArrayNoise.OffsetIntegrals;
        long before = GC.GetTotalAllocatedBytes(true);
        var run = Run("Port:P1 f 0 Num=1 Z=50 Ohm\n" + line + "\nanalysis NF1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz Noise=yes\n");
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        double cs = Data(run, "NF1").Cubes["Cs"].ComplexValues[0].Real;
        string note = run.Notes.Single(s => s.Contains("integrated once per element offset", StringComparison.Ordinal));
        output.WriteLine($"Cs at the feed {cs:E6} W/Hz; {allocated / 1e6:F0} MB allocated in all; {note}");
        Assert.Equal(pairs, ArrayNoise.PairIntegrals);
        Assert.Equal(dense, ArrayCorrelation.DenseCopies);
        Assert.Equal(127 * 127, ArrayNoise.OffsetIntegrals - offsets);
        Assert.True(cs > 0);

        // The offset table against the pair-by-pair integral of the same sky over the same pattern, on the 16×16 corner.
        var p = new Dictionary<string, CircuitRF.Core.Expressions.Value>(StringComparer.Ordinal)
        {
            ["Record"] = new(Record("e5")), ["Mode"] = new("feed"), ["Rows"] = new(64.0), ["Cols"] = new(64.0), ["dx"] = new(25.86e-3), ["dy"] = new(25.86e-3),
            ["RecordRows"] = new(5.0), ["RecordCols"] = new(5.0), ["RecordDx"] = new(25.86e-3), ["RecordDy"] = new(25.86e-3), ["Patterns"] = new("centre"),
        };
        var model = CircuitRF.Core.Devices.PhasedArrayModel.Create(p);
        var rad = model.Radiation(F);
        var table = new double[(int)Math.Round(90 / ArraySky.StepDeg) + 1];
        for (int i = 0; i < table.Length; i++) table[i] = 5 + 280 * Math.Exp(-(i * ArraySky.StepDeg * Math.PI / 180) / 0.0873);
        var sky = new ArraySky(table, 290, "test");
        int reference = model.ReferencePattern(rad);
        var transform = new ArraySkyTransform(rad, model.Lattice, F, sky, ArrayNoise.MeanBrightness(rad, sky, reference), reference);
        var byOffset = transform.Table();
        var term = new ArrayOffsetTerm(model.Lattice, byOffset, _ => true);
        double scale = byOffset.Values.Max(v => v.Magnitude), worst = 0;
        var lattice = model.Lattice;
        var corner = Enumerable.Range(0, 16).SelectMany(r => Enumerable.Range(0, 16).Select(c => r * 64 + c)).ToArray();
        // every pair of the corner through the term's own lookup; the pair integral taken once per displacement (pairs sharing one are one number)
        (long, long) Shift(int i, int j)
        {
            var (xi, yi) = lattice.Position(i);
            var (xj, yj) = lattice.Position(j);
            return ((long)Math.Round((xi - xj) * 1e9), (long)Math.Round((yi - yj) * 1e9));
        }
        var first = new Dictionary<(long, long), (int I, int J)>();
        foreach (int i in corner)
            foreach (int j in corner)
                if (j != i) first.TryAdd(Shift(i, j), (i, j));
        var keys = first.Keys.ToArray();
        var values = new Complex[keys.Length];
        Parallel.For(0, keys.Length, q => values[q] = transform.Pair(first[keys[q]].I, first[keys[q]].J));
        var integral = keys.Select((k, q) => (k, q)).ToDictionary(t => t.k, t => values[t.q]);
        foreach (int i in corner)
            foreach (int j in corner)
                if (j != i) worst = Math.Max(worst, (term[i, j] - integral[Shift(i, j)]).Magnitude / scale);
        output.WriteLine($"16×16 corner: {corner.Length * (corner.Length - 1)} pairs, {integral.Count} displacements integrated pair by pair, worst |table − pair| " +
                         $"{worst:E2} of the largest offset term");
        Assert.True(worst <= 1e-9, $"worst {worst:E2}");
    }

    /// <summary>Gate 2 — a uniform sky at the array's own temperature (300 K, the ambient) is the Bosma noise a passive block stamps: the
    /// noise-wave correlation of a coupled 3×3 array's nine ports equals the EM Field Data block's on the same record to 1e-12.</summary>
    [Fact]
    public void AUniformSky_AtItsOwnTemperature_IsTheBosmaNoiseOfThePassiveBlock()
    {
        const string sp = "analysis SP1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz Noise=yes\n";
        var block = Data(Run(Ports(9) + $"EmFieldData:EM1 {Nets(9)} File=\"{Record("e33")}\"\n" + sp), "SP1").Cubes["Cs"].ComplexValues;
        var array = Data(Run(Ports(9) + $"PhasedArray:PA1 {Nets(9)} Record=\"{Record("e33")}\" Rows=3 Cols=3 dx=25.86 mm dy=25.86 mm Level=exact " +
                             "RecordRows=3 RecordCols=3 RecordDx=25.86 mm RecordDy=25.86 mm Tsky=300 Tground=300\n" + sp), "SP1").Cubes["Cs"].ComplexValues;
        double scale = block.Max(c => c.Magnitude), worst = 0;
        for (int i = 0; i < block.Length; i++) worst = Math.Max(worst, (block[i] - array[i]).Magnitude / scale);
        output.WriteLine($"Cs: largest {scale:E4} W/Hz, worst difference {worst:E2} of it");
        Assert.True(worst <= 1e-12, $"worst {worst:E2}");
        // and coupled: an off-diagonal entry is a sizeable fraction of the diagonal
        Assert.True(block[1].Magnitude > 1e-3 * scale);
    }
}
