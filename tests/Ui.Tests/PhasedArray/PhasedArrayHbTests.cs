// brief-paa-6 — brute-force harmonic balance: a fields analysis whose waves come from an HB analysis (waves=HB1), each amplifier's active
// reflection, and the loci on a Smith chart. Gates 1-4, on the shipped bench testdata/phased-array/hb-1x4/Array Bench.cnl (four copies of the
// Harmonic Balance example's FET amplifier, retuned for 5.9 GHz, on PAA-1's E3 record), with its sweeps replaced by each gate's own.

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Circuit;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayHbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa6-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Fixture(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", name));

    /// <summary>The shipped bench's circuit, with its record path made absolute and its analyses and measures left out, then <paramref name="tail"/>.</summary>
    private static string Bench(string tail, double pinDbm, double theta0Deg)
    {
        string text = File.ReadAllText(Fixture(Path.Combine("hb-1x4", "Array Bench.cnl")));
        text = text.Replace("File=\"../results/e3x.cemr\"", $"File=\"{Fixture(Path.Combine("results", "e3x.cemr"))}\"");
        var lines = text.Split('\n').Where(l => !l.StartsWith("analysis ", StringComparison.Ordinal) && !l.StartsWith("measure ", StringComparison.Ordinal))
                        .Select(l => l.StartsWith("Pin = ", StringComparison.Ordinal) ? $"Pin = {pinDbm.ToString(CultureInfo.InvariantCulture)}"
                                   : l.StartsWith("theta0 = ", StringComparison.Ordinal) ? $"theta0 = {theta0Deg.ToString(CultureInfo.InvariantCulture)} deg" : l);
        return string.Join("\n", lines) + "\n" + tail;
    }

    private RunResult Run(string text)
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest());
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    private static DataSet Data(RunResult run, string name) => run.Results.Single(r => r.Name == name).Data;

    private const string Hb = "analysis HB1 type=hb Tone=\"f0\" ToneUnit=GHz MaxHarm=5 FFTOverSample=1 Tol=1e-9 DriveStepping=IfNecessary\n";
    private const string FromHb = "analysis F1 type=fields block=EM1 waves=HB1\n";

    /// <summary>The worst difference between two patterns after the one complex factor that best maps the second onto the first, over the first's peak.</summary>
    private static double PatternDifference(DataSet a, DataSet b)
    {
        Complex[] Field(DataSet d) => [.. d.Cubes["farfield.Etheta"].ComplexValues, .. d.Cubes["farfield.Ephi"].ComplexValues];
        var x = Field(a);
        var y = Field(b);
        Complex num = 0;
        double den = 0;
        for (int i = 0; i < x.Length; i++) { num += Complex.Conjugate(y[i]) * x[i]; den += y[i].Magnitude * y[i].Magnitude; }
        Complex r = num / den;
        double peak = x.Max(c => c.Magnitude), worst = 0;
        for (int i = 0; i < x.Length; i++) worst = Math.Max(worst, (x[i] - r * y[i]).Magnitude);
        return worst / peak;
    }

    /// <summary>The record's own S at its one frequency, from its Touchstone file (RI, 50 ohm).</summary>
    private static Complex[,] RecordS()
    {
        var numbers = File.ReadAllLines(Fixture(Path.Combine("results", "e3x.s4p")))
                          .Where(l => l.Trim().Length > 0 && l.TrimStart()[0] is not ('!' or '#'))
                          .SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        var s = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) s[i, j] = new Complex(numbers[1 + 2 * (4 * i + j)], numbers[2 + 2 * (4 * i + j)]);
        return s;
    }

    /// <summary>Gate 1 — far below compression the PAs are linear, so the pattern from HB's waves is the linear fields analysis's on the same bench, and each
    /// element's active reflection is equation (3) from the record's S and the linear analysis's waves. Steered to 30°, so the elements differ.
    /// <para>At -60 dBm, 80 dB below compression. The amplifier is biased near pinch-off and its gain moves with drive, so the difference is its own
    /// nonlinearity and falls as the drive squared: 1.1e-5 of the pattern's peak at -18 dBm (40 dB below compression), 7e-10 at -60.</para>
    /// <para>Beside it sits a floor that RISES as the drive falls: HB's back-solve injects the linear side's own current, which carries the
    /// Newton residual left at round-off (an absolute ~1e-14 A that no tighter Tol removes), so relative to the signal it grows as the signal
    /// shrinks. Measured: 1.97e-9 at -70 dBm, 1.10e-9 at -60, 7.09e-9 at -50, where the nonlinearity takes over. -60 is near the
    /// minimum of the two, and the gate allows both (2e-9).</para></summary>
    [Fact]
    public void SmallSignal_HbWavesPatternAndActiveReflection_AreTheLinearOnes()
    {
        var run = Run(Bench(Hb + FromHb + "analysis F2 type=fields block=EM1 freq=5.9 freqUnit=GHz drive=1,2,3,4\n", -60, 30));
        var hb = Data(run, "F1");
        var lin = Data(run, "F2");
        double pattern = PatternDifference(hb, lin);
        output.WriteLine($"pattern: worst difference {pattern:E2} of the peak");

        var s = RecordS();
        var a = lin.Cubes["a"].ComplexValues;
        var gamma = hb.Cubes["array.GammaActive"].ComplexValues;
        double worstGamma = 0;
        for (int n = 0; n < 4; n++)
        {
            Complex b = 0;
            for (int m = 0; m < 4; m++) b += s[n, m] * a[m];
            Complex eq3 = b / a[n];
            worstGamma = Math.Max(worstGamma, (gamma[n] - eq3).Magnitude);
            output.WriteLine($"element {n + 1}: HB {gamma[n].Magnitude:F6}∠{gamma[n].Phase * 180 / Math.PI:F3}°, (3) {eq3.Magnitude:F6}∠{eq3.Phase * 180 / Math.PI:F3}°");
        }
        output.WriteLine($"active reflection: worst difference {worstGamma:E2}");
        Assert.True(pattern <= 2e-9, $"pattern differs by {pattern:E2}");
        Assert.True(worstGamma <= 2e-9, $"active reflection differs by {worstGamma:E2}");
    }

    /// <summary>A node's or a branch's spectrum at one harmonic, from an HB result's V or I cube, by its label.</summary>
    private static Complex At(DataSet hb, string cube, string label, int k)
    {
        var c = hb.Cubes[cube];
        int at = Array.IndexOf(c.Axes[0].Labels!, label);
        Assert.True(at >= 0, $"{cube} has no {label}");
        return c.ComplexValues[at * c.Axes[1].Length + k];
    }

    /// <summary>Each amplifier's output power and PAE, read on its own instance.</summary>
    private static (double Pout, double Pae)[] Amplifiers(DataSet hb)
        => [.. Enumerable.Range(1, 4).Select(n =>
        {
            double pout = 0.5 * (At(hb, "V", $"e{n}", 1) * Complex.Conjugate(At(hb, "I", $"PA{n}.Iout", 1))).Real;
            double pin = 0.5 * (At(hb, "V", $"g{n}", 1) * Complex.Conjugate(At(hb, "I", $"PA{n}.Iin", 1))).Real;
            double pdc = (At(hb, "V", $"PA{n}.vdd", 0) * At(hb, "I", $"PA{n}.Idc", 0)).Real;
            return (pout, (pout - pin) / pdc * 100);
        })];

    private static string Z(Complex z) => $"({z.Real.ToString("G17", CultureInfo.InvariantCulture)}+j*({z.Imaginary.ToString("G17", CultureInfo.InvariantCulture)}))";

    /// <summary>Gate 2 — at a compressed point each PA sees its active load: with the block replaced by four ideal one-ports presenting, at the fundamental,
    /// the impedance of the HB run's GammaActive (and at each harmonic the impedance that run's own voltage and current there give), every PA's output
    /// power and PAE are the array run's to HB's convergence tolerance (Tol=1e-9 A on both).</summary>
    [Fact]
    public void Compressed_EachAmplifierLoadedByItsActiveReflection_IsTheArrayRun()
    {
        var array = Run(Bench(Hb + FromHb, 22, 30));
        var hb = Data(array, "HB1");
        var gamma = Data(array, "F1").Cubes["array.GammaActive"].ComplexValues;
        var bench = Bench(Hb, 22, 30).Split('\n').Where(l => !l.StartsWith("EmFieldData:EM1", StringComparison.Ordinal)).ToList();
        for (int n = 1; n <= 4; n++)
        {
            Complex z1 = 50 * (1 + gamma[n - 1]) / (1 - gamma[n - 1]);
            var zk = Enumerable.Range(2, 4).Select(k => At(hb, "V", $"e{n}", k) / At(hb, "I", $"PA{n}.Iout", k)).ToArray();
            bench.Add($"Z_Port:ZL{n} e{n} 0 Z[1,1]=if(freq<1.5*f0,{Z(z1)},if(freq<2.5*f0,{Z(zk[0])},if(freq<3.5*f0,{Z(zk[1])},if(freq<4.5*f0,{Z(zk[2])},{Z(zk[3])}))))");
        }
        var loaded = Data(Run(string.Join("\n", bench) + "\n"), "HB1");

        var want = Amplifiers(hb);
        var got = Amplifiers(loaded);
        double worstPout = 0, worstPae = 0;
        for (int n = 0; n < 4; n++)
        {
            output.WriteLine($"PA{n + 1}: |Γ_act| {gamma[n].Magnitude:F4}, Pout {10 * Math.Log10(want[n].Pout * 1000):F4} / {10 * Math.Log10(got[n].Pout * 1000):F4} dBm, " +
                             $"PAE {want[n].Pae:F5} / {got[n].Pae:F5} %");
            worstPout = Math.Max(worstPout, Math.Abs(got[n].Pout - want[n].Pout) / want[n].Pout);
            worstPae = Math.Max(worstPae, Math.Abs(got[n].Pae - want[n].Pae));
        }
        output.WriteLine($"worst: Pout {worstPout:E2} relative, PAE {worstPae:E2} pp");
        Assert.True(worstPout <= 1e-6, $"Pout differs by {worstPout:E2}");
        Assert.True(worstPae <= 1e-4, $"PAE differs by {worstPae:E2} pp");
    }

    /// <summary>Gate 3 — harmonic=2 asks for 11.8 GHz, which the record (5.9 GHz only) does not hold: refused, naming the nearest frequency it does.</summary>
    [Fact]
    public void ASecondHarmonicTheRecordDoesNotHold_IsRefused_NamingTheNearest()
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(Bench(Hb + "analysis F1 type=fields block=EM1 waves=HB1 harmonic=2\n", 0, 0), _dir, _dir),
                                             new CircuitEvaluationRequest());
        string said = run.StatusMessage + " " + string.Join(" ", run.Warnings) + " " + string.Join(" ", run.Convergence.Select(c => c.Detail));
        output.WriteLine(said);
        Assert.Contains("11.8 GHz", said);
        Assert.Contains("the nearest frequency it holds is 5.9 GHz", said);
        Assert.DoesNotContain(run.Results, r => r.Name == "F1");
    }

    /// <summary>Gate 4 — a sweep of theta0 over {0, 15, 30, 45}° around HB1 gives the fields analysis four patterns and four sets of active reflections, and
    /// a Smith chart trace of the active reflection against theta0 draws four loci of four points, each labelled by its element.</summary>
    [Fact]
    public void ASweepAroundTheHbAnalysis_GivesAPatternPerPoint_AndOneLocusPerElement()
    {
        var run = Run(Bench(Hb.Replace("Tol=1e-9", "Tol=1e-6") + FromHb + "analysis SW1 type=parametric_sweep Var=theta0 Values=0,15,30,45 Unit=deg Inner=HB1\n", 16, 0));
        var f1 = Data(run, "F1");
        var et = f1.Cubes["farfield.Etheta"];
        Assert.Equal("theta0", et.Axes[0].Name);
        Assert.Equal(4, et.Axes[0].Length);
        int per = et.ComplexValues.Length / 4;
        var patterns = Enumerable.Range(0, 4).Select(p => et.ComplexValues.Skip(p * per).Take(per).ToArray()).ToArray();
        for (int p = 1; p < 4; p++)
            Assert.True(patterns[p].Zip(patterns[0]).Max(z => (z.First - z.Second).Magnitude) > 1e-3 * patterns[0].Max(c => c.Magnitude), $"point {p}'s pattern is point 0's");
        var gamma = f1.Cubes["array.GammaActive"];
        Assert.Equal(["theta0", "f", "element"], gamma.Axes.Select(a => a.Name));
        Assert.Equal([4, 1, 4], gamma.Axes.Select(a => a.Length));

        var t = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex)
        {
            CubeName = "array.GammaActive",
            Slice = [new AxisSlice("theta0", AxisRole.KeepAsX, 0), new AxisSlice("f", AxisRole.PinToIndex, 0), new AxisSlice("element", AxisRole.FamilyIterate, 0)],
        };
        TraceResolve.SetCubeDataFrom(t, f1, PlotType.Smith, FreqUnit.GHz);
        Assert.Null(t.ExpressionError);
        Assert.Equal(4, t.FamilyCurves.Count);
        Assert.All(t.FamilyCurves, c => Assert.Equal(4, c.Points.Count));
        Assert.True(t.LabelsFamilyCurves);
        Assert.Equal(["element = 1", "element = 2", "element = 3", "element = 4"], Enumerable.Range(0, 4).Select(t.FamilyCurveLabel));
        // each locus is that element's reflection: its last point is element 4 at 45° (a plot keeps its points in single precision)
        var last = gamma.ComplexValues[3 * 4 + 3];
        Assert.Equal(last.Real, t.FamilyCurves[3].Points[3].X, 6);
        Assert.Equal(last.Imaginary, t.FamilyCurves[3].Points[3].Y, 6);
    }

    /// <summary>waves=, harmonic= and (brief-paa-6b) dirs= are written and read back by the .cnl and by the schematic's own analysis record, so a bench edited in either keeps them.</summary>
    [Fact]
    public void WavesAndHarmonic_SurviveTheCnlAndTheSchematicRecord()
    {
        string path = Path.Combine(_dir, "b.cnl");
        File.WriteAllText(path, "R:R1 a 0 R=50 Ohm\nanalysis F1 type=fields block=EM1 waves=HB1 harmonic=2 dirs=\"peak;30,0\"\n");
        var fa = (Core.Design.FieldsAnalysis)Core.Netlist.CnlReader.ReadFile(path).TestBench.Analyses.Single();
        Assert.Equal(("HB1", "2", "peak;30,0"), (fa.Waves, fa.HarmonicExpr, fa.DirsExpr));

        var tb = new Core.Design.TestBench("tb");
        tb.Analyses.Add(fa);
        File.WriteAllText(path, Core.Netlist.CnlWriter.Write(tb));
        var again = (Core.Design.FieldsAnalysis)Core.Netlist.CnlReader.ReadFile(path).TestBench.Analyses.Single();
        Assert.Equal(("HB1", "2", "peak;30,0"), (again.Waves, again.HarmonicExpr, again.DirsExpr));

        var json = CircuitRF.Design.Schematic.AnalysisSerialization.Serialize([fa], []);
        var back = (Core.Design.FieldsAnalysis)CircuitRF.Design.Schematic.AnalysisSerialization.Deserialize(json).Analyses.Single();
        Assert.Equal(("HB1", "2", "peak;30,0"), (back.Waves, back.HarmonicExpr, back.DirsExpr));
    }
}
