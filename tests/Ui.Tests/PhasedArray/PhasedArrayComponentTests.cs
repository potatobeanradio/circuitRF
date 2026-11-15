// brief-paa-3 — the Phased Antenna Array component in Elements mode, through the schematic path (CircuitEvaluation on .cnl text): gates
// 1-5. Fixtures: testdata/phased-array (5.9 GHz, 5° grid; its README). None of them records its ports' positions, so every line states
// RecordRows/RecordCols/RecordDx/RecordDy, as a user of an older record does.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine.AntennaArray;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayComponentTests(ITestOutputHelper output) : IDisposable
{
    private const double F = 5.9e9, Pitch = 25.86e-3;
    private static readonly double K = 2 * Math.PI * F / CircuitRF.Engine.Mom.EmConstants.C0;
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa3-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Fixture(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", name));

    private static string Record(string name) => Fixture(Path.Combine("results", name + ".cemr"));

    private RunResult Evaluate(string text) => CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest());

    private Dictionary<string, DataSet> Run(string text)
    {
        var run = Evaluate(text);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run.Results.ToDictionary(r => r.Name, r => r.Data);
    }

    private string Refusal(string text)
    {
        var run = Evaluate(text);
        Assert.NotEqual(RunStatus.Success, run.Status);
        return run.StatusMessage + " " + string.Join(" ", run.Convergence.Select(c => c.Detail)) + " " + string.Join(" ", run.Warnings);
    }

    private static string Ports(int n) => string.Join("\n", Enumerable.Range(1, n).Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm")) + "\n";

    private static string Nets(int n) => string.Join(' ', Enumerable.Range(1, n).Select(k => $"p{k}"));

    /// <summary>Phase-shifter steering of a rows × cols lattice at <paramref name="pitchM"/> to θ0 in φ = 0, as weight VARs and a drive list.</summary>
    private static string Steered(int rows, int cols, double pitchM, double theta0Deg, string block = "PA1")
    {
        var lattice = new ArrayLattice(rows, cols, pitchM, pitchM);
        double u0 = Math.Sin(theta0Deg * Math.PI / 180);
        var lines = new List<string>();
        for (int n = 0; n < lattice.Count; n++)
        {
            var w = Complex.FromPolarCoordinates(1, -K * lattice.Position(n).X * u0);
            lines.Add($"W{n + 1} = {w.Real:R} + j*({w.Imaginary:R})");
        }
        lines.Add($"analysis F1 type=fields block={block} freq=5.9 freqUnit=GHz drive={string.Join(',', Enumerable.Range(1, lattice.Count))} " +
                  $"weights={string.Join(',', Enumerable.Range(1, lattice.Count).Select(k => $"W{k}"))}");
        return string.Join("\n", lines) + "\n";
    }

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

    /// <summary>Gate 1 — Level=exact on the 1×4 fixture is the EM Field Data block it replaces: the same S and the same pattern, to 1e-12.</summary>
    [Fact]
    public void Exact_IsTheEmFieldDataBlockItReplaces()
    {
        string drives = Steered(1, 4, Pitch, 30);
        var block = Run(Ports(4) + $"EmFieldData:EM1 {Nets(4)} File=\"{Record("e3x")}\"\n" + drives.Replace("block=PA1", "block=EM1") +
                        "analysis SP1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz\n");
        var array = Run(Ports(4) + $"PhasedArray:PA1 {Nets(4)} Record=\"{Record("e3x")}\" Rows=1 Cols=4 dx=25.86 mm dy=25.86 mm Level=exact " +
                        "RecordRows=1 RecordCols=4 RecordDx=25.86 mm RecordDy=25.86 mm\n" + drives +
                        "analysis SP1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz\n");
        Same(block["SP1"].Cubes["S"], array["SP1"].Cubes["S"], 1e-12, "S");
        Same(block["F1"].Cubes["farfield.Etheta"], array["F1"].Cubes["farfield.Etheta"], 1e-12, "E_theta");
        Same(block["F1"].Cubes["farfield.Ephi"], array["F1"].Cubes["farfield.Ephi"], 1e-12, "E_phi");
        Assert.Equal(block["F1"].Cubes["farfield.RealizedGainDbi"].RealValues[0], array["F1"].Cubes["farfield.RealizedGainDbi"].RealValues[0], 9);
        Assert.Equal(4, array["F1"].Cubes["array.GammaActive"].ComplexValues.Length);
    }

    /// <summary>Gate 2 — 25.86 mm, 1018.11 mil and 0.02586 in metres are one pitch: the same pattern to 1e-12.</summary>
    [Fact]
    public void APitch_InAnyUnit_IsOnePitch()
    {
        string Line(string dx) => Ports(4) + $"PhasedArray:PA1 {Nets(4)} Record=\"{Record("e5")}\" Rows=2 Cols=2 dx={dx} dy=25.86 mm " +
                                  "RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm\n" + Steered(2, 2, Pitch, 20);
        var mm = Run(Line("25.86 mm"))["F1"].Cubes["farfield.Etheta"];
        // 1018.11 mil is 25.860 mm to the digits written, so it is held to its own rounding (2e-7 relative).
        var mil = Run(Line("1018.11 mil"))["F1"].Cubes["farfield.Etheta"];
        var metre = Run(Line("0.02586"))["F1"].Cubes["farfield.Etheta"];
        Same(mm, metre, 1e-12, "metres");
        Same(mm, mil, 1e-5, "mil");
        var exact = Run(Line($"{25.86e-3 / 2.54e-5:R} mil"))["F1"].Cubes["farfield.Etheta"];
        Same(mm, exact, 1e-12, "mil, to every digit");
        var lambda = Run("f0 = 5.9 GHz\n" + Line($"{25.86e-3 / (CircuitRF.Engine.Mom.EmConstants.C0 / 5.9e9):R}*lambda(f0)"))["F1"].Cubes["farfield.Etheta"];
        Same(mm, lambda, 1e-12, "lambda(f0)");
    }

    /// <summary>Gate 3 — a sweep of a VAR driving dx returns a pattern per point whose array factor is the direct sum at that pitch; the
    /// pitch warning names only the points past the threshold.</summary>
    [Fact]
    public void ASweepOfThePitch_ReEvaluatesTheArray_AndWarnsOnlyWhereItShould()
    {
        double[] pitches = [25.86, 26.5, 29.0];   // 0, 0.0126 and 0.0614 λ from the record's at 5.9 GHz
        string text = Ports(4) + "pitch = 25.86 mm\n" +
                      $"PhasedArray:PA1 {Nets(4)} Record=\"{Record("e5")}\" Rows=1 Cols=4 dx=pitch dy=25.86 mm " +
                      "RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm\n" +
                      "analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1,2,3,4 weights=1,1,1,1\n" +
                      $"analysis SW1 type=parametric_sweep Var=pitch Values={string.Join(',', pitches)} Unit=mm Inner=F1\n";
        var run = Evaluate(text);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        // The sweep's result is named after the fields analysis it wraps, its cubes stacked over pitch.
        var data = run.Results.Single(r => r.Name == "F1").Data;
        var et = data.Cubes["farfield.Etheta"];
        var a = data.Cubes["array.a"].ComplexValues;
        var grid = Grid(et);
        int perPoint = et.ComplexValues.Length / pitches.Length;
        var e5 = Open("e5");
        var sub = new ArrayLattice(5, 5, Pitch, Pitch);
        var classes = ArrayCoupling.Classes(new ArrayLattice(1, 4, Pitch, Pitch), sub);
        var patterns = classes.Select(c => CircuitRF.Design.Em3d.EmbeddedPatterns.Referred(e5, c, F, sub.Position(c))).ToArray();
        var dirs = ArrayDirections.OnGrid(grid);
        for (int p = 0; p < pitches.Length; p++)
        {
            var lattice = new ArrayLattice(1, 4, pitches[p] * 1e-3, Pitch);
            // The direct sum: an exponential per element and direction, at this point's pitch, of the elements' own patterns and waves.
            var got = et.ComplexValues.Skip(p * perPoint).Take(perPoint).ToArray();
            var want = new Complex[perPoint];
            for (int d = 0; d < perPoint; d++)
                for (int n = 0; n < 4; n++)
                    want[d] += a[p * 4 + n] * patterns[n].ETheta[d] * Complex.FromPolarCoordinates(1, K * lattice.Position(n).X * dirs.U[d]);
            // The cube is referred to 1 V at port 1, the direct sum to the waves' own scale: one complex constant apart.
            int top = Array.IndexOf(got, got.MaxBy(c => c.Magnitude));
            Complex scale = got[top] / want[top];
            double peak = got.Max(c => c.Magnitude), worst = 0;
            for (int d = 0; d < perPoint; d++) worst = Math.Max(worst, (got[d] - scale * want[d]).Magnitude);
            Assert.True(worst / peak <= 1e-12, $"pitch {pitches[p]} mm: worst {worst / peak:E2}");
        }
        string warnings = string.Join("\n", run.Warnings);
        output.WriteLine(warnings);
        Assert.DoesNotContain("dx is 25.86 mm", warnings);
        Assert.DoesNotContain("dx is 26.5 mm", warnings);
        Assert.Contains("dx is 29 mm and the record's is 25.86 mm", warnings);
    }

    private static CircuitRF.Design.Em3d.PortFarFieldSet Open(string name) => CircuitRF.Design.Em3d.PortFarFieldSet.Open(Record(name)).Set!;

    private static CircuitRF.Engine.Mom.PlanarFarFieldGrid Grid(DataCube cube)
        => new(cube.Axes.First(x => x.Name == "theta").Values, cube.Axes.First(x => x.Name == "phi").Values);

    /// <summary>Gate 4 — the refusals: a swept Rows in Elements mode names the VAR; 9×9 names Feed mode; exact on another lattice names both.</summary>
    [Fact]
    public void TheRefusals_SayWhatIsWrong()
    {
        string head = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";
        string swept = Refusal(Ports(4) + "nr = 2\n" + $"PhasedArray:PA1 {Nets(4)} {head} Rows=nr Cols=2\n" +
                               "analysis SP1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz\nanalysis SW1 type=parametric_sweep Var=nr Values=2,3 Inner=SP1\n");
        Assert.Contains("sweep of nr moves its Rows", swept);
        string big = Refusal(Ports(81) + $"PhasedArray:PA1 {Nets(81)} {head} Rows=9 Cols=9\nanalysis SP1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz\n");
        Assert.Contains("81 elements", big);
        Assert.Contains("Feed mode", big);
        string exact = Refusal(Ports(49) + $"PhasedArray:PA1 {Nets(49)} {head} Rows=7 Cols=7 Level=exact\nanalysis SP1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz\n");
        Assert.Contains("5 × 5 rectangular", exact);
        Assert.Contains("7 × 7 rectangular", exact);
    }

    /// <summary>Gate 5 — the embedded level through the schematic adds nothing to the engine's answer: the 6×6 from the 5×5 reproduces
    /// PAA-2 gate 3's numbers (the harness's reference), and a 7×7's pattern and active reflections are the engine's own.</summary>
    [Fact]
    public void Embedded_ThroughTheSchematic_IsTheEngine()
    {
        var reference = JsonDocument.Parse(File.ReadAllText(Fixture("paa2_reference.json"))).RootElement;
        var scan = reference.GetProperty("scans").EnumerateArray().First(s => s.GetProperty("theta0").GetDouble() == 30 && s.GetProperty("phi0").GetDouble() == 0);
        var want = scan.GetProperty("levels").GetProperty("L1k");
        string head = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";
        var six = Run(Ports(36) + $"PhasedArray:PA1 {Nets(36)} {head} Rows=6 Cols=6\n" + Steered(6, 6, Pitch, 30))["F1"];
        var gamma = six.Cubes["array.GammaActive"].ComplexValues;
        var re = want.GetProperty("gammaRe").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var im = want.GetProperty("gammaIm").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        double worst = Enumerable.Range(0, 36).Max(n => (gamma[n] - new Complex(re[n], im[n])).Magnitude);
        output.WriteLine($"6×6 from the 5×5 at 30°: worst |ΔΓ_act| against the harness {worst:E2}");
        Assert.True(worst <= 1e-9, $"Γ_act {worst:E2} from the harness");

        // 7×7: the component's pattern, normalised away, is the engine's sum of the same referred patterns with the same waves.
        var seven = Run(Ports(49) + $"PhasedArray:PA1 {Nets(49)} {head} Rows=7 Cols=7\n" + Steered(7, 7, Pitch, 20))["F1"];
        var a = seven.Cubes["array.a"].ComplexValues;
        var e5 = Open("e5");
        var sub = new ArrayLattice(5, 5, Pitch, Pitch);
        var lattice = new ArrayLattice(7, 7, Pitch, Pitch);
        var classes = ArrayCoupling.Classes(lattice, sub);
        var distinct = classes.Distinct().ToArray();
        var elements = ArrayElements.Classes([.. distinct.Select(p => CircuitRF.Design.Em3d.EmbeddedPatterns.Referred(e5, p, F, sub.Position(p)))],
                                             [.. classes.Select(c => Array.IndexOf(distinct, c))]);
        var field = ArrayPattern.Sum(lattice, a, elements, ArrayDirections.OnGrid(e5.Grid), F);
        var got = seven.Cubes["farfield.Etheta"].ComplexValues;
        int top = Array.IndexOf(got, got.MaxBy(c => c.Magnitude));
        Complex scale = got[top] / field.ETheta[top];
        double peak = got.Max(c => c.Magnitude), err = 0;
        for (int d = 0; d < got.Length; d++) err = Math.Max(err, (got[d] - scale * field.ETheta[d]).Magnitude / peak);
        Assert.True(err <= 1e-12, $"7×7 pattern {err:E2} from the engine's");
        var ext = ArrayCoupling.Extend(Sub(e5), sub, lattice, classes: true);
        var active = ArrayWaves.Active(ext.S, a);
        var g7 = seven.Cubes["array.GammaActive"].ComplexValues;
        double gErr = Enumerable.Range(0, 49).Max(n => (g7[n] - active.Gamma[n]!.Value).Magnitude);
        Assert.True(gErr <= 1e-9, $"7×7 Γ_act {gErr:E2} from the engine's");
    }

    /// <summary>R-paa3-6 — the beam's measurement functions read PAA-2's local evaluation of the array's own pattern and waves: each equals
    /// the engine's number for the same elements and incident waves.</summary>
    [Fact]
    public void TheBeamFunctions_AreTheEnginesLocalEvaluation()
    {
        string head = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";
        var circuit = PreparedCircuit.FromText(Ports(36) + $"PhasedArray:PA1 {Nets(36)} {head} Rows=6 Cols=6\n" + Steered(6, 6, Pitch, 30), _dir, _dir);
        string[] functions = ["HpbwDeg(F1, \"elevation\")", "HpbwDeg(F1, \"azimuth\")", "SllDb(F1)", "PointingErrorDeg(F1, 30, 0)",
                              "ScanLossDb(F1)", "GratingMarginDeg(F1)", "PowerBalance(F1)", "ActiveVswrMax(F1)"];
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Expressions = functions });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var got = run.Expressions.Select(e => e.Value?.AsReal() ?? throw new Xunit.Sdk.XunitException(e.Error)).ToArray();

        var data = run.Results.Single(r => r.Name == "F1").Data;
        var a = data.Cubes["array.a"].ComplexValues;
        var gamma = data.Cubes["array.GammaActive"].ComplexValues;
        var e5 = Open("e5");
        var sub = new ArrayLattice(5, 5, Pitch, Pitch);
        var lattice = new ArrayLattice(6, 6, Pitch, Pitch);
        var classes = ArrayCoupling.Classes(lattice, sub);
        var distinct = classes.Distinct().ToArray();
        var elements = ArrayElements.Classes([.. distinct.Select(p => CircuitRF.Design.Em3d.EmbeddedPatterns.Referred(e5, p, F, sub.Position(p)))],
                                             [.. classes.Select(c => Array.IndexOf(distinct, c))]);
        var dirs = ArrayDirections.OnGrid(e5.Grid);
        var overview = ArrayPattern.Sum(lattice, a, elements, dirs, F);
        int best = Enumerable.Range(0, dirs.Count).MaxBy(overview.Power);
        var beam = ArrayBeam.Evaluate(lattice, a, elements, F, dirs.ThetaRad[best] * 180 / Math.PI, dirs.PhiRad[best] * 180 / Math.PI);
        var broad = ArrayBeam.Evaluate(lattice, [.. a.Select(x => new Complex(x.Magnitude, 0))], elements, F, 0, 0);
        double inc = a.Sum(x => x.Magnitude * x.Magnitude);
        var lobe = ArrayBeam.GratingLobe(lattice, F, beam.PeakThetaDeg, beam.PeakPhiDeg);
        double accepted = Enumerable.Range(0, 36).Sum(n => 0.5 * a[n].Magnitude * a[n].Magnitude * (1 - gamma[n].Magnitude * gamma[n].Magnitude));
        double worstGamma = gamma.Max(g => g.Magnitude);
        double[] want =
        [
            beam.ElevationBeamwidthDeg, beam.AzimuthBeamwidthDeg, beam.FirstSidelobeDb!.Value,
            ArrayBeam.Separation(ArrayDirections.Vector(beam.PeakThetaDeg * Math.PI / 180, beam.PeakPhiDeg * Math.PI / 180), ArrayDirections.Vector(Math.PI / 6, 0)) * 180 / Math.PI,
            ArrayBeam.ScanLossDb(beam.PeakPower, inc, broad.PeakPower, inc),
            Math.Asin(Math.Sin(beam.PeakThetaDeg * Math.PI / 180) + lobe.Margin) * 180 / Math.PI - beam.PeakThetaDeg,
            ArrayWaves.RadiatedPowerW(overview) / accepted,
            (1 + worstGamma) / (1 - worstGamma),
        ];
        output.WriteLine(string.Join("; ", functions.Select((f, k) => $"{f} = {got[k]:G6}")));
        for (int k = 0; k < want.Length; k++) Assert.True(Math.Abs(got[k] - want[k]) <= 1e-9 * Math.Max(1, Math.Abs(want[k])), $"{functions[k]}: {got[k]} against {want[k]}");
    }

    /// <summary>The 5×5's S from its Touchstone, as the component reads it.</summary>
    private static Complex[,] Sub(CircuitRF.Design.Em3d.PortFarFieldSet e5)
    {
        var snp = RfCore.TouchstoneIO.ReadFile(Fixture(Path.Combine("results", "e5.s25p")), readComments: false).Matrices[0];
        var s = new Complex[25, 25];
        for (int i = 0; i < 25; i++)
            for (int j = 0; j < 25; j++) s[i, j] = snp[i, j];
        _ = e5;
        return s;
    }
}
