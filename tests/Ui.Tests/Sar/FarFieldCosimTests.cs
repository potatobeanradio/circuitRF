// ================================================================
//  FarFieldCosimTests.cs — brief-cosim-pattern-3's routine gates, on a small far field built here with no solver: the pattern of a whole
//  circuit from a result's per-port patterns, the circuit's S beside it, and what it refuses. And brief-cosim-pattern-4's: the same pattern
//  under a slider, a goal and the optimizer.
// ================================================================

using System.Collections.Concurrent;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Tuning;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed partial class FarFieldCosimTests : IDisposable
{
    private const double F0 = 1e9, F1 = 2e9, Z0 = 50;
    private const double Tolerance = 1e-9;
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-cp3-").FullName;
    private string Record => Path.Combine(_dir, "ems.cemr");
    private Complex? _s22;
    private double _fieldScale = 1;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ── the result ──────────────────────────────────────────────────────────────────────────────────

    private static readonly PlanarFarFieldGrid Grid = FarFieldStage.Sphere(30, 45);
    private static readonly double[] Hz = [F0, F1];

    /// <summary>A reciprocal lossy three-port, different at each frequency.</summary>
    private Complex[,] S(int f, int n = 3)
    {
        double scale = f == 0 ? 1 : 0.8;
        Complex[,] all =
        {
            { new(0.30, -0.20), new(0.10, 0.05), new(-0.05, 0.12) },
            { new(0.10, 0.05), new(-0.25, 0.10), new(0.08, -0.03) },
            { new(-0.05, 0.12), new(0.08, -0.03), new(0.15, 0.22) },
        };
        var s = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) s[i, j] = all[i, j] * scale;
        if (_s22 is { } shorted) s[1, 1] = shorted;
        return s;
    }

    /// <summary>Port k's pattern at frequency f: random but repeatable, and small enough to radiate what the block accepts.</summary>
    private (Complex[] Theta, Complex[] Phi) Stored(int f, int k)
    {
        var r = new Random(1000 * f + k);
        Complex C() => _fieldScale * 0.3 * new Complex(r.NextDouble() - 0.5, r.NextDouble() - 0.5);
        var t = new Complex[Grid.DirectionCount];
        var p = new Complex[Grid.DirectionCount];
        for (int d = 0; d < t.Length; d++) { t[d] = C(); p[d] = C(); }
        return (t, p);
    }

    private void WriteResult(int n = 3, bool farField = true, double[]? z0 = null, string solver = "test", bool marked = false)
    {
        Directory.CreateDirectory(_dir);
        z0 ??= [.. Enumerable.Repeat(Z0, n)];
        var data = new DataSet();
        Axis[] sAxes = [new Axis("f", Hz, "Hz"), new Axis("i", [.. Enumerable.Range(1, n).Select(k => (double)k)], ""), new Axis("j", [.. Enumerable.Range(1, n).Select(k => (double)k)], "")];
        var values = new Complex[2 * n * n];
        for (int f = 0; f < 2; f++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) values[(f * n + i) * n + j] = S(f, n)[i, j];
        data.AddToGroup("em", "S", new DataCube(sAxes, values));
        data.AddToGroup("em", "Z0", DataSetBuilder.BuildZ0Cube([.. z0.Select(v => new Complex(v, 0))]));
        if (farField)
        {
            var points = new List<Em3dPatternPoint>();
            for (int f = 0; f < 2; f++)
                for (int k = 0; k < n; k++)
                {
                    var (t, p) = Stored(f, k);
                    points.Add(new Em3dPatternPoint(FarFieldStage.Pattern(Grid, t, p, k + 1, Hz[f]), 1e-3, S(f, n)[k, k]));
                }
            Em3dRadiation.Publish(data, "test", Grid, [.. Enumerable.Range(1, n)], Hz, points, 3.0, hemisphere: false);
            if (marked) data.AddToGroup(PlanarFarField.Group, Em3dRadiation.PortsTerminatedCube, DataCube.Scalar(1.0));
        }
        EmResultRecord.StampData(data, "stamp-1");
        using (var npy = File.Create(Path.Combine(_dir, "ems_em.npy"))) DataSetExporter.ExportNpy(data, npy);

        string snp = $"ems.s{n}p";
        var lines = new List<string> { "! " + EmProvenanceStamp.StampPrefix + "stamp-1", $"# Hz S RI R {Z0}" };
        for (int f = 0; f < 2; f++)
        {
            var row = new List<string> { Hz[f].ToString("R") };
            var s = S(f, n);
            // Touchstone's own quirk: a 2-port's second column is S21, every other N-port's row order is by row
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) row.Add($"{(n == 2 ? s[j, i] : s[i, j]).Real:R} {(n == 2 ? s[j, i] : s[i, j]).Imaginary:R}");
            lines.Add(string.Join(' ', row));
        }
        File.WriteAllLines(Path.Combine(_dir, snp), lines);
        File.WriteAllText(Record, new EmResultRecord
        {
            Stamp = "stamp-1", Written = DateTimeOffset.Now, Solver = new() { Name = solver },
            Files = new() { Snp = snp, Npy = "ems_em.npy" },
            Ports = [.. Enumerable.Range(1, n).Select(k => new EmResultPort { Number = k, Name = $"P{k}", Z0Re = z0[k - 1] })],
            Frequencies = new() { SweepHz = [.. Hz], FarFieldHz = farField ? [.. Hz] : [] },
        }.Serialize());
    }

    private string Block(params string[] nets) => $"EmFieldData:EM1 {string.Join(' ', nets)} File=\"{Record}\"\n";

    private RunResult Run(string text, bool measurements = false)
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest
        {
            Measurements = measurements, Analyses = text.Contains("type=sparam") ? ["SP1", "F1"] : null,
        });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    private static void Close(Complex want, Complex got, string what)
        => Assert.True((want - got).Magnitude <= Tolerance * Math.Max(1, want.Magnitude), $"{what}: wanted {want}, got {got}");

    /// <summary>The cube's pattern at one frequency and its one port, in the grid's order.</summary>
    private static Complex[] PatternOf(DataSet data, string cube, int frequency)
    {
        var c = data.Cubes[cube];
        Assert.Equal(["freq", "theta", "phi", "port"], c.Axes.Select(a => a.Name));
        return c.ComplexValues[(frequency * Grid.DirectionCount)..((frequency + 1) * Grid.DirectionCount)];
    }

    // ── G1, G2: the sum ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void G1_EveryPortOnACircuitPortOfItsOwnZ0_DriveK_GivesThatPortsStoredPattern(int drive)
    {
        WriteResult();
        var data = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\nPort:P3 c 0 Num=3 Z=50 Ohm\n" + Block("a", "b", "c") +
                       $"analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive={drive}\n").Results.Single().Data;
        var (t, p) = Stored(0, drive - 1);
        var theta = PatternOf(data, "farfield.Etheta", 0);
        var phi = PatternOf(data, "farfield.Ephi", 0);
        for (int d = 0; d < t.Length; d++)
        {
            Close(t[d], theta[d], $"Eθ[{d}]");
            Close(p[d], phi[d], $"Eφ[{d}]");
        }
    }

    public static TheoryData<string, string, Func<double, Complex>> Loads => new()
    {
        { "L", "L:L1 b 0 L=5n", w => new Complex(0, w * 5e-9) },
        { "C", "C:C1 b 0 C=1p", w => 1 / new Complex(0, w * 1e-12) },
        { "SRLC", "SRLC:Z1 b 0 R=10 L=5n C=2p", w => new Complex(10, w * 5e-9 - 1 / (w * 2e-12)) },
        { "PRLC", "PRLC:Z1 b 0 R=200 L=5n C=2p", w => 1 / new Complex(1 / 200.0, w * 2e-12 - 1 / (w * 5e-9)) },
    };

    [Theory]
    [MemberData(nameof(Loads))]
    public void G2_PartsAcrossTwoPorts_GiveTheSumWrittenFromTheAlgebra(string kind, string part, Func<double, Complex> z)
    {
        _ = kind;
        WriteResult();
        // The feed on port 1, the part on port 2, 75 Ω on port 3
        var data = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nR:R3 c 0 R=75\n" + part + "\n" + Block("a", "b", "c") +
                       "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n").Results.Single().Data;
        Compare(data, z(2 * Math.PI * F0), 75);
    }

    [Fact]
    public void G2_AnS2pWiredOneToNodeTwoToGround_IsTheImpedanceItPresents()
    {
        WriteResult();
        // An ideal attenuator-free two-port: port 2 grounded, so it presents Z11 with port 2 shorted
        Complex[,] two = { { new(0.2, 0.1), new(0.6, -0.1) }, { new(0.6, -0.1), new(-0.3, 0.2) } };
        string file = Path.Combine(_dir, "part.s2p");
        File.WriteAllLines(file, ["# Hz S RI R 50", Row(F0 * 0.5), Row(F0), Row(F0 * 1.5)]);
        string Row(double f) => $"{f:R} {two[0, 0].Real:R} {two[0, 0].Imaginary:R} {two[1, 0].Real:R} {two[1, 0].Imaginary:R} {two[0, 1].Real:R} {two[0, 1].Imaginary:R} {two[1, 1].Real:R} {two[1, 1].Imaginary:R}";
        var data = Run($"Port:P1 a 0 Num=1 Z=50 Ohm\nR:R3 c 0 R=75\nSnP:X1 b 0 0 NumPorts=2 File=\"{file}\"\n" + Block("a", "b", "c") +
                       "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n").Results.Single().Data;
        // Γ with port 2 shorted (Γ2 = −1): S11 − S12·S21/(1 + S22)
        Complex gamma = two[0, 0] - two[0, 1] * two[1, 0] / (1 + two[1, 1]);
        Compare(data, Z0 * (1 + gamma) / (1 - gamma), 75);
    }

    /// <summary>a = (I − ΓS)⁻¹·a_source with port 1 fed, port 2 on z2 and port 3 on z3, written out here and not through the circuit solver.</summary>
    private void Compare(DataSet data, Complex z2, double z3)
    {
        var s = S(0);
        Complex G(Complex z) => (z - Z0) / (z + Z0);
        Complex[] gamma = [Complex.Zero, G(z2), G(z3)];
        var m = new Complex[3, 4];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) m[i, j] = (i == j ? Complex.One : Complex.Zero) - gamma[i] * s[i, j];
            m[i, 3] = i == 0 ? Complex.One : Complex.Zero;
        }
        for (int c = 0; c < 3; c++)
        {
            int pivot = Enumerable.Range(c, 3 - c).MaxBy(r => m[r, c].Magnitude);
            for (int x = 0; x < 4; x++) (m[c, x], m[pivot, x]) = (m[pivot, x], m[c, x]);
            for (int r = 0; r < 3; r++)
            {
                if (r == c) continue;
                Complex factor = m[r, c] / m[c, c];
                for (int x = c; x < 4; x++) m[r, x] -= factor * m[c, x];
            }
        }
        Complex[] a = [m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2]];

        // Port 1's total wave: the feed has no reflection of its own, so its voltage is √Z0·(a₁ + b₁)
        Complex b1 = s[0, 0] * a[0] + s[0, 1] * a[1] + s[0, 2] * a[2];
        Complex v1 = Math.Sqrt(Z0) * (a[0] + b1);
        var theta = PatternOf(data, "farfield.Etheta", 0);
        for (int d = 0; d < Grid.DirectionCount; d++)
        {
            Complex want = Complex.Zero;
            for (int k = 0; k < 3; k++) want += a[k] * Math.Sqrt(Z0) * (1 + s[k, k]) * Stored(0, k).Theta[d];
            Close(want / v1, theta[d], $"Eθ[{d}]");
        }
        Close(b1 / a[0], data.Cubes[FieldCosimulation.ReflectionCube].ComplexValues[0], "Γ");
    }

    // ── R-cp3-4 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreqAll_TakesEveryStoredFrequency_AndABetweenFrequencyIsRefusedListingThem()
    {
        WriteResult();
        const string Around = "Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=50\nR:R3 c 0 R=50\n";
        var all = Run(Around + Block("a", "b", "c") + "analysis F1 type=fields block=EM1 freq=all drive=1\n").Results.Single().Data;
        Assert.Equal(Hz, all.Cubes["farfield.U"].Axes[0].Values);
        for (int d = 0; d < Grid.DirectionCount; d += 7) Close(Stored(1, 0).Theta[d], PatternOf(all, "farfield.Etheta", 1)[d], $"Eθ[{d}]");
        Assert.Equal(Hz, all.Cubes["S"].Axes[0].Values);

        var between = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(Around + Block("a", "b", "c") +
            "analysis F1 type=fields block=EM1 freq=1.5 freqUnit=GHz drive=1\n", _dir, _dir), new CircuitEvaluationRequest());
        Assert.Contains("1 GHz, 2 GHz", between.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("not at 1.5 GHz", between.StatusMessage, StringComparison.Ordinal);
    }

    // ── R-cp3-5: the name ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDottedName_SurvivesTheNpyAndTheTraceSpecParser_AndReadsAsAPattern()
    {
        WriteResult();
        var run = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=50\nR:R3 c 0 R=50\n" + Block("a", "b", "c") + "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n");
        string file = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(run.GroupedResults!, file, ExportFormat.Npy, new ExportOptions(Format: ExportFormat.Npy));
        var back = DataSetImporter.Import(file).DataSet;

        Assert.True(back.Contains("F1.farfield.U"));
        Assert.Equal(run.GroupedResults!["F1.farfield.U"].RealValues, back["F1.farfield.U"].RealValues);
        Assert.True(CubeTraceSpecParser.TryParse("dB10(F1.farfield.U[0, :, 0, 1])", back, out string cube, out _, out _, out string error), error);
        Assert.Equal("F1.farfield.U", cube);
        Assert.True(LevelReference.IsReferencedLevel(back, "F1.farfield.PeakEirpDbm", out double reference));
        Assert.Equal(3.0, reference);
        Assert.True(TraceRowViewModel.IsFarFieldCube("F1.farfield.U"));
        Assert.False(TraceRowViewModel.IsFarFieldCube("F1.U"));
        Assert.Equal(CubeTransform.dB10, TraceRowViewModel.DefaultTransformFor(back["F1.farfield.U"], PlotType.Rect, "F1.farfield.U"));
    }

    // ── G3: S, and the refusals ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void G3_F1S_IsSP1S_AndReadsInAMeasurement()
    {
        WriteResult();
        var run = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nC:C1 b 0 C=1p\nR:R3 c 0 R=50\n" + Block("a", "b", "c") +
                      "analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz\nanalysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n" +
                      "measure M1 = dB(F1.S(1,1)) - dB(SP1.S(1,1))\n", measurements: true);
        var data = run.GroupedResults!;
        Assert.Equal(data["SP1.S"].Axes.Select(a => a.Name), data["F1.S"].Axes.Select(a => a.Name));
        Assert.Equal(data["SP1.S"].ComplexValues, data["F1.S"].ComplexValues);
        Assert.Equal(data["SP1.Z0"].ComplexValues, data["F1.Z0"].ComplexValues);
        Assert.Equal(0.0, data["M1"].RealValues[0]);
    }

    private string RefusalOf(string fields, string around = "Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=50\n", params string[] nets)
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(around + Block(nets.Length > 0 ? nets : ["a", "b"]) + fields, _dir, _dir), new CircuitEvaluationRequest());
        return run.StatusMessage + string.Join(' ', run.Convergence.Select(c => c.Detail));
    }

    [Fact]
    public void G3_ARecordWithNoFarField_IsRefusedNamingBothSettings()
    {
        WriteResult(2, farField: false);
        string why = RefusalOf("analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n");
        Assert.Contains("Radiation pattern", why, StringComparison.Ordinal);
        Assert.Contains("Keep per-port fields", why, StringComparison.Ordinal);
    }

    [Fact]
    public void G3_PortsWhoseZ0Differ_AreRefusedNamingTheFile()
    {
        WriteResult(2, z0: [50, 200]);
        string why = RefusalOf("analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n");
        Assert.Contains("ems_em.npy", why, StringComparison.Ordinal);
        Assert.Contains("50 Ω, 200 Ω", why, StringComparison.Ordinal);
    }

    [Fact]
    public void G3_ARecordWithAFarFieldAnNpyWithout_IsRefusedNamingTheFile()
    {
        WriteResult(2);
        WriteResultNpyWithoutFarField();
        string why = RefusalOf("analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n");
        Assert.Contains("ems_em.npy", why, StringComparison.Ordinal);
        Assert.Contains("Radiation pattern", why, StringComparison.Ordinal);
    }

    private void WriteResultNpyWithoutFarField()
    {
        string path = Path.Combine(_dir, "ems_em.npy");
        var kept = DataSetImporter.Import(path).DataSet;
        var data = new DataSet();
        foreach (string g in kept.Groups.Where(g => g != PlanarFarField.Group))
            foreach (var (n, c) in kept.CubesIn(g)) data.AddToGroup(g, n, c);
        using var npy = File.Create(path);
        DataSetExporter.ExportNpy(data, npy);
    }

    [Theory]
    [InlineData("openEMS", "an older openEMS result")]
    [InlineData("EMerge", "an EMerge result")]
    public void AnUncorrectedResult_IsAskedForAgain_AndACorrectedOneRuns(string solver, string named)
    {
        const string Bench = "Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=75\n";
        const string Fields = "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n";
        WriteResult(2, solver: solver);
        string why = RefusalOf(Fields);
        Assert.Contains(named, why, StringComparison.Ordinal);
        Assert.Contains("Run its EM setup again", why, StringComparison.Ordinal);

        WriteResult(2, solver: solver, marked: true);
        Assert.True(Run(Bench + Block("a", "b") + Fields).Results.Single().Data.Cubes.ContainsKey("farfield.U"));
    }

    [Fact]
    public void OneOpenEmsPort_HasNothingToTakeOut_SoAnOlderResultRuns()
    {
        WriteResult(1, solver: "openEMS");
        Assert.True(Run("Port:P1 a 0 Num=1 Z=50 Ohm\n" + Block("a") + "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n")
            .Results.Single().Data.Cubes.ContainsKey("farfield.U"));
    }

    [Fact]
    public void FdtdExcitations_LeaveAnIncidentWaveAtOnePort_AndOneVoltThere()
    {
        Complex[,] u = { { new(3, 1), new(0.4, -0.2) }, { new(-0.3, 0.1), new(2, -2) } };
        Complex[,] i = { { new(0.02, 0.01), new(-0.004, 0.002) }, { new(0.006, 0), new(0.03, 0.01) } };
        Complex[] z0 = [new(50, 0), new(200, 0)];
        var each = CircuitRF.Engine.Em3d.FdtdPortExcitations.From(u, i, z0, out _)!;
        for (int c = 0; c < 2; c++)
        {
            Complex voltage = Complex.Zero, current = Complex.Zero;
            for (int k = 0; k < 2; k++) { voltage += u[c, k] * each.Weights[k, c]; current += i[c, k] * each.Weights[k, c]; }
            Close(Complex.One, voltage, "the port's own voltage");
            Close(each.Current[c], current, "the port's own current");
            int other = 1 - c;
            Complex wave = Complex.Zero;
            for (int k = 0; k < 2; k++) wave += (u[other, k] + z0[other] * i[other, k]) * each.Weights[k, c];
            Close(Complex.Zero, wave, "the other port's incident wave");
        }
    }

    [Fact]
    public void G3_AFieldMeasurementOnAFarFieldAlone_SaysThereIsNoFieldSet()
    {
        WriteResult(2);
        var run = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=50\n" + Block("a", "b") + "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\nmeasure M1 = Pabs(F1)\n", measurements: true);
        Assert.Contains(run.Warnings.Concat(run.Notes).Concat([run.StatusMessage]), s => s.Contains("Keep per-port fields", StringComparison.Ordinal));
    }

    [Fact]
    public void G3_FreqAllOnAFieldSetAlone_IsRefusedNamingItsFrequency()
    {
        new MemoryFieldSet().WriteBeside(_dir, 300e6, "stamp-1");
        string why = RefusalOf("analysis F1 type=fields block=EM1 freq=all drive=1\n", "Port:P1 in 0 Num=1 Z=50 Ohm\nC:C1 p2 0 C=2e-12\n", "in", "p2");
        Assert.Contains("freq=all", why, StringComparison.Ordinal);
        Assert.Contains("300 MHz", why, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldSetAlone_KeepsItsResult_AndGainsTheCircuitsS()
    {
        new MemoryFieldSet().WriteBeside(_dir, 300e6, "stamp-1");
        var data = Run("Port:P1 in 0 Num=1 Z=50 Ohm\nC:C1 p2 0 C=2e-12\n" + Block("in", "p2") +
                       "analysis F1 type=fields block=EM1 freq=300 freqUnit=MHz drive=1 average=no\n").Results.Single().Data;
        Assert.True(data.Cubes.ContainsKey("S"));
        Assert.True(data.Cubes.ContainsKey(FieldCosimulation.IncidentCube));
        Assert.DoesNotContain(data.Cubes.Keys, k => k.StartsWith("farfield.", StringComparison.Ordinal));
        Assert.Equal(data.Cubes[FieldCosimulation.ReflectionCube].ComplexValues[0], data.Cubes["S"].ComplexValues[0]);
    }

    private const double Sar = 300e6;

    /// <summary>A result with a field set at <see cref="Sar"/> and a far field at 0.8 of it and at it.</summary>
    private void WriteResultWithBoth()
    {
        new MemoryFieldSet().WriteBeside(_dir, Sar, "stamp-1");
        string npyPath = Path.Combine(_dir, "ems_em.npy");
        var data = DataSetImporter.Import(npyPath).DataSet;
        data.AddToGroup("em", "Z0", DataSetBuilder.BuildZ0Cube([new Complex(Z0, 0), new Complex(Z0, 0)]));
        var (t, p) = Stored(0, 0);
        var one = new Em3dPatternPoint(FarFieldStage.Pattern(Grid, t, p, 1, Sar), 1e-3, Complex.Zero);
        var two = new Em3dPatternPoint(FarFieldStage.Pattern(Grid, t, p, 2, Sar), 1e-3, Complex.Zero);
        var three = new Em3dPatternPoint(FarFieldStage.Pattern(Grid, t, p, 1, 0.8 * Sar), 1e-3, Complex.Zero);
        var four = new Em3dPatternPoint(FarFieldStage.Pattern(Grid, t, p, 2, 0.8 * Sar), 1e-3, Complex.Zero);
        Em3dRadiation.Publish(data, "test", Grid, [1, 2], [0.8 * Sar, Sar], [three, four, one, two], 0, hemisphere: false);
        using (var npy = File.Create(npyPath)) DataSetExporter.ExportNpy(data, npy);
        var (record, _) = EmResultRecord.Load(Record);
        record!.Frequencies.FarFieldHz = [0.8 * Sar, Sar];
        File.WriteAllText(Record, record.Serialize());
    }

    [Fact]
    public void ARecordWithBoth_GivesBothInOneAnalysis_AndSaysWhichHalfAFrequencyLacks()
    {
        WriteResultWithBoth();
        const string Around = "Port:P1 in 0 Num=1 Z=50 Ohm\nC:C1 p2 0 C=2e-12\n";
        var both = Run(Around + Block("in", "p2") + "analysis F1 type=fields block=EM1 freq=300 freqUnit=MHz drive=1 average=no\n").Results.Single().Data;
        Assert.True(both.Cubes.ContainsKey("farfield.U"));
        Assert.True(both.Cubes.ContainsKey(FieldCosimulation.PeakLocalCube));
        Assert.True(both.Cubes.ContainsKey("S"));

        var farOnly = Run(Around + Block("in", "p2") + "analysis F1 type=fields block=EM1 freq=240 freqUnit=MHz drive=1 average=no\n");
        Assert.True(farOnly.Results.Single().Data.Cubes.ContainsKey("farfield.U"));
        Assert.False(farOnly.Results.Single().Data.Cubes.ContainsKey(FieldCosimulation.PeakLocalCube));
        Assert.Contains(farOnly.Notes, n => n.Contains("no SAR or B1", StringComparison.Ordinal));

        var all = Run(Around + Block("in", "p2") + "analysis F1 type=fields block=EM1 freq=all drive=1\n");
        Assert.Equal(2, all.Results.Single().Data.Cubes["farfield.U"].Axes[0].Values.Length);
        Assert.False(all.Results.Single().Data.Cubes.ContainsKey(FieldCosimulation.PeakLocalCube));
    }

    // ── R-cp3-1, R-cp3-7 ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASecondEvaluation_ReadsTheNpyNoAgain_AndNormAvailableIsSaid()
    {
        WriteResult(2);
        const string Bench = "Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=50\n";
        string fields = "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1 norm=available\n";
        var first = Run(Bench + Block("a", "b") + fields);
        long reads = PortFarFieldSet.Reads;
        Run(Bench + Block("a", "b") + fields);
        Assert.Equal(reads, PortFarFieldSet.Reads);
        Assert.Contains("norm=available does not apply to the pattern", first.Notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void APortNearlyAShort_IsANote()
    {
        _s22 = new Complex(-0.998, 0);
        WriteResult(2);
        var run = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=50\n" + Block("a", "b") + "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n");
        Assert.Contains(run.Notes, n => n.Contains("port 2's |1 + S| is 0.002", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Warnings, w => w.Contains("|1 + S|", StringComparison.Ordinal));
    }

    [Fact]
    public void APatternRadiatingMoreThanTheBlockIsDelivered_IsAWarning()
    {
        _fieldScale = 10;
        WriteResult(2);
        var run = Run("Port:P1 a 0 Num=1 Z=50 Ohm\nR:R2 b 0 R=50\n" + Block("a", "b") + "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n");
        Assert.Contains(run.Warnings, w => w.Contains("of the power delivered into the block", StringComparison.Ordinal));
    }

    // ── brief-cosim-pattern-4 ───────────────────────────────────────────────────────────────────────

    private const string Around = "Port:P1 a 0 Num=1 Z=50 Ohm\nC:C1 b 0 C=1p\nR:R3 c 0 R=75\n";
    private const string AtF0 = "analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n";
    private static readonly string[] PatternFunctions = ["RealizedGainDbi", "GainDbi", "DirectivityDbi", "RadiationEfficiency", "TrpDbm", "PeakEirpDbm"];

    private PreparedCircuit Circuit(string analysis = AtF0)
        => PreparedCircuit.FromText(Around + Block("a", "b", "c") + analysis, _dir, _dir);

    private static RunResult Evaluate(PreparedCircuit circuit, FieldsOutput output, Dictionary<string, string>? values = null, params string[] expressions)
    {
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Fields = output, Tunables = values, Expressions = expressions });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        return run;
    }

    private sealed class Sink : ITuneResultSink
    {
        public DataSet? Last;
        public void Publish(DataSet data) => Last = data;
        public void Commit(DataSet data, IReadOnlyDictionary<string, string> values) { }
        public void Drop() { }
        public void Snapshot() { }
        public void ClearSnapshot() { }
    }

    private static DataSet TunedBy(PreparedCircuit circuit, Dictionary<string, string> values, bool final)
    {
        var sink = new Sink();
        var ui = new ConcurrentQueue<Action>();
        using var session = TuneSession.ForCircuit(circuit, sink, ui.Enqueue);
        session.Request(values, final);
        while (session.IsEvaluating || !ui.IsEmpty)
        {
            Thread.Yield();
            while (ui.TryDequeue(out var a)) a();
        }
        return sink.Last ?? throw new InvalidOperationException("the session published nothing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASlidersEvaluation_IsSimulate_CubeForCube(bool released)
    {
        WriteResult();
        var values = new Dictionary<string, string> { ["C1.C"] = "2.5 pF" };
        var circuit = Circuit();
        var tuned = TunedBy(circuit, values, released);
        var simulated = Evaluate(circuit, FieldsOutput.Written, values).GroupedResults!;

        var names = simulated.CubesIn("F1").Keys.ToArray();
        Assert.Contains("farfield.U", names);
        Assert.Equal(names.Order(), tuned.CubesIn("F1").Keys.Order());
        foreach (string name in names)
        {
            var (want, got) = (simulated.CubesIn("F1")[name], tuned.CubesIn("F1")[name]);
            if (want.DataKind == DataKind.Complex) Assert.Equal(want.ComplexValues, got.ComplexValues);
            else Assert.Equal(want.RealValues, got.RealValues);
        }
        Assert.NotEqual(Evaluate(circuit, FieldsOutput.Written).GroupedResults!["F1.farfield.U"].RealValues, tuned["F1.farfield.U"].RealValues);
    }

    [Fact]
    public void OnDemand_FormsNoCube_SumsOnlyWhenRead_AndEachFunctionIsItsCube()
    {
        WriteResult();
        var circuit = Circuit();
        var written = Evaluate(circuit, FieldsOutput.Written).GroupedResults!;

        long sums = CosimPatternMetrics.Sums;
        var idle = Evaluate(circuit, FieldsOutput.OnDemand).GroupedResults!;
        Assert.Equal(sums, CosimPatternMetrics.Sums);
        Assert.DoesNotContain(idle.CubesIn("F1").Keys, k => k.StartsWith("farfield.", StringComparison.Ordinal));
        Assert.True(idle.Contains("F1." + FieldCosimulation.IncidentCube));
        Assert.Equal(written["F1.S"].ComplexValues, idle["F1.S"].ComplexValues);

        var read = Evaluate(circuit, FieldsOutput.OnDemand, null, [.. PatternFunctions.Select(f => $"{f}(F1)")]);
        Assert.Equal(sums + 1, CosimPatternMetrics.Sums);
        for (int i = 0; i < PatternFunctions.Length; i++)
        {
            Assert.Null(read.Expressions[i].Error);
            Assert.Equal(written[$"F1.farfield.{PatternFunctions[i]}"].RealValues[0], read.Expressions[i].Value!.Value.AsReal());
        }

        // Simulate's measurements read the same numbers from the sum it already made.
        var simulated = Evaluate(circuit, FieldsOutput.Written, null, "RealizedGainDbi(F1)");
        Assert.Equal(written["F1.farfield.RealizedGainDbi"].RealValues[0], simulated.Expressions[0].Value!.Value.AsReal());
        Assert.Equal(sums + 1, CosimPatternMetrics.Sums);
    }

    [Theory]
    [InlineData("RealizedGainDbi")]
    [InlineData("GainDbi")]
    [InlineData("DirectivityDbi")]
    public void ADirection_IsThePeakLessTheDropToIt(string function)
    {
        WriteResult();
        var circuit = Circuit();
        var u = Evaluate(circuit, FieldsOutput.Written).GroupedResults!["F1.farfield.U"].RealValues;
        var run = Evaluate(circuit, FieldsOutput.OnDemand, null, $"{function}(F1)", $"{function}(F1, 30, 45)", $"{function}(F1, 30, 405)", $"{function}(F1, 30, -315)");
        double peak = run.Expressions[0].Value!.Value.AsReal(), at = run.Expressions[1].Value!.Value.AsReal();
        Assert.Equal(peak + 10 * Math.Log10(u[Grid.IndexOf(1, 1)] / u.Max()), at, 9);
        Assert.Equal(at, run.Expressions[2].Value!.Value.AsReal());
        Assert.Equal(at, run.Expressions[3].Value!.Value.AsReal());
    }

    [Fact]
    public void ThePatternFunctions_RefuseWhatTheyCannotAnswer_ByName()
    {
        WriteResult();
        string ErrorOf(string analysis, string expression)
            => Evaluate(Circuit(analysis), FieldsOutput.OnDemand, null, expression).Expressions[0].Error ?? "";

        Assert.Contains("is not on its pattern's grid, which is theta 0 to 180 in steps of 30", ErrorOf(AtF0, "GainDbi(F1, 31, 45)"), StringComparison.Ordinal);
        string every = ErrorOf("analysis F1 type=fields block=EM1 freq=all drive=1\n", "RealizedGainDbi(F1)");
        Assert.Contains("RealizedGainDbi(F1) is one number", every, StringComparison.Ordinal);
        Assert.Contains("at one of those frequencies", every, StringComparison.Ordinal);
        Assert.Contains("psSAR", ErrorOf(AtF0, "psSAR(F1, 10)"), StringComparison.Ordinal);

        Directory.Delete(_dir, true);
        new MemoryFieldSet().WriteBeside(_dir, 300e6, "stamp-1");
        var run = Evaluate(PreparedCircuit.FromText("Port:P1 in 0 Num=1 Z=50 Ohm\nC:C1 p2 0 C=2e-12\n" + Block("in", "p2") +
                                                    "analysis F1 type=fields block=EM1 freq=300 freqUnit=MHz drive=1 average=no\n", _dir, _dir),
                           FieldsOutput.OnDemand, null, "TrpDbm(F1)");
        Assert.Contains("reads a far field", run.Expressions[0].Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>A one-port block behind a series L: with the block's own R left, the reflection is least where L cancels its reactance, so the best
    /// realized gain is at a value known by hand.</summary>
    [Fact]
    public void TheOptimizer_MovesAPartToTheKnownBest_OnTheGain_StartingNoEmRun()
    {
        WriteResult(n: 1);
        Complex gamma = S(0, 1)[0, 0];
        Complex z = Z0 * (1 + gamma) / (1 - gamma);
        double bestNh = -z.Imaginary / (2 * Math.PI * F0) * 1e9;

        var circuit = PreparedCircuit.FromText("Port:P1 a 0 Num=1 Z=50 Ohm\nL:L1 a b L=2 nH\n" + Block("b") + AtF0 +
                                               "tune L1.L min=0.5 nH max=15 nH opt=1\ngoal G = RealizedGainDbi(F1) analysis=F1 ge 100\noptimize algorithm=auto seed=1\n", _dir, _dir);
        _ = PortFarFieldSet.Open(Record);
        long launches = OpenEmsRun.Invocations, reads = PortFarFieldSet.Reads, runs = FieldCosimulation.Runs;
        var result = OptimizationRun.Create(circuit, new OptimizationOptions()).Run();

        Assert.Equal(OptimizationOutcome.GoalsUnmet, result.Outcome);
        Assert.True(TunableValue.TryParse(result.BestValues["L1.L"], out _, out _, out double henries));
        Assert.Equal(bestNh, henries * 1e9, bestNh * 0.01);
        Assert.Equal(launches, OpenEmsRun.Invocations);
        Assert.Equal(reads, PortFarFieldSet.Reads);
        Assert.True(FieldCosimulation.Runs - runs >= result.Evaluations / 2, "the evaluations ran through the recombination");
    }

    [Fact]
    public void TheGoalEditor_ListsWhatTheRecordHolds()
    {
        string[] Ids(PreparedCircuit c) => [.. GoalTemplates.For(c).Where(t => t.Group == GoalTemplateGroup.Fields).Select(t => t.Id)];

        WriteResult();
        var far = Ids(Circuit());
        Assert.Contains("F1.realizedgain", far);
        Assert.DoesNotContain("F1.pssar", far);
        Assert.Empty(Ids(Circuit("analysis F1 type=fields block=EM1 freq=all drive=1\n")));
        var template = GoalTemplates.For(Circuit()).Single(t => t.Id == "F1.realizedgaindir").Make();
        Assert.Equal("RealizedGainDbi(F1, 0, 0)", template.Expression);
        Assert.Equal("F1", GoalTemplates.AnalysisReferencedBy(template.Expression, Circuit().Tb!));

        Directory.Delete(_dir, true);
        new MemoryFieldSet().WriteBeside(_dir, 300e6, "stamp-1");
        var set = Ids(Circuit());
        Assert.Contains("F1.pssar", set);
        Assert.DoesNotContain("F1.realizedgain", set);

        // Nothing to read says nothing, so both are offered and the run says what is wrong.
        File.Delete(Record);
        var unread = Ids(Circuit());
        Assert.Contains("F1.pssar", unread);
        Assert.Contains("F1.realizedgain", unread);
    }

    // R-cp4-2 — the three plots a pattern is read on, each resolved as the window resolves it.
    [Theory]
    [InlineData(PlotType.Polar, "dB10(F1.farfield.U[0, :, 0, 1])")]
    [InlineData(PlotType.Rect, "dB10(F1.farfield.U[0, :, 0, 1])")]
    [InlineData(PlotType.Surface3D, "dB10(F1.farfield.U[0, :, :, 1])")]
    public async Task AnOpenPatternPlot_FollowsTheSlider_AndKeepsItsSettings(PlotType type, string spec)
    {
        WriteResult();
        var circuit = Circuit();
        var first = Evaluate(circuit, FieldsOutput.Written).GroupedResults!;
        string path = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(first, path, ExportFormat.Npy);

        var vm = new DataDisplayDocumentViewModel();
        var lib = vm.Window.DataSourceLibrary;
        lib.ResultsRootProvider = () => _dir;
        await lib.LoadFileAsync(path);
        var loaded = lib.Entries.Single().Data!;
        var plot = vm.Window.DataDisplay!.Plots[0].PlotVM.Plot;
        plot.SetPlotType(type);
        plot.PolarRadial = PolarRadialMode.Db;
        Assert.True(CubeTraceSpecParser.TryParse(spec, loaded, out string cube, out var slice, out var transform, out string error), error);
        var trace = new Trace(new SNP([F0], 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = path, CubeName = cube, Slice = slice, Transform = transform };
        plot.Traces.Add(trace);
        PlotInspectorViewModel.TrySetCubeData(trace, lib, type, FreqUnit.GHz);
        plot.PolarDbFloor = -33;
        plot.AutoscaleX = false;
        plot.AutoscaleY = false;
        var camera = plot.SurfaceCamera;
        var pinned = trace.Slice.ToArray();
        object? Shape() => type == PlotType.Surface3D ? trace.SurfaceGrid : trace.Points.ToArray();
        var before = Shape();

        Assert.True(lib.Publish(path, TunedBy(circuit, new() { ["C1.C"] = "2.5 pF" }, final: false)));

        var after = Shape();
        Assert.False(ReferenceEquals(before, after));
        if (type == PlotType.Surface3D) Assert.NotEqual(((PatternSurfaceGrid)before!).Db, ((PatternSurfaceGrid)after!).Db);
        else Assert.NotEqual((System.Collections.IEnumerable)before!, (System.Collections.IEnumerable)after!);
        Assert.Equal(-33, plot.PolarDbFloor);
        Assert.False(plot.AutoscaleX);
        Assert.False(plot.AutoscaleY);
        Assert.Equal(camera, plot.SurfaceCamera);
        Assert.Equal(pinned, trace.Slice);
    }

    // R-cp4-1 — an optimizer's best point holds no pattern cubes, and a pattern plot must not empty for it: it keeps the file's, dimmed, until the final point.
    [Fact]
    public async Task AnOptimizersBestPoint_LeavesAnOpenPatternDimmed_AndTheFinalPointRestoresIt()
    {
        WriteResult();
        var circuit = Circuit();
        var file = Evaluate(circuit, FieldsOutput.Written).GroupedResults!;
        string path = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(file, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);

        var values = new Dictionary<string, string> { ["C1.C"] = "2.5 pF" };
        lib.Publish(path, Evaluate(circuit, FieldsOutput.OnDemand, values).GroupedResults!, DataSourceLibraryViewModel.OptimizingChip, partial: true);
        var shown = lib.Entries.Single().Data!;
        Assert.Equal(file["F1.farfield.U"].RealValues, shown["F1.farfield.U"].RealValues);
        Assert.Equal(["F1.farfield"], lib.StaleGroupsFor(path));
        Assert.NotEqual(file["F1.a"].ComplexValues, shown["F1.a"].ComplexValues);

        var final = Evaluate(circuit, FieldsOutput.Written, values).GroupedResults!;
        lib.Publish(path, final, DataSourceLibraryViewModel.OptimizingChip, partial: false);
        Assert.Empty(lib.StaleGroupsFor(path));
        Assert.Equal(final["F1.farfield.U"].RealValues, lib.Entries.Single().Data!["F1.farfield.U"].RealValues);
    }

    // ── brief-cosim-pattern-5: a parametric sweep wraps the pattern ───────────────────────────────

    private string Swept(string fields, string sweep = "analysis SW1 type=parametric_sweep Var=Lv Start=1 Stop=9 Step=4 Unit=nH Inner=F1\n", string lv = "Lv = 5 nH")
        => $"{lv}\nPort:P1 a 0 Num=1 Z=50 Ohm\nL:L1 b 0 L=Lv\nR:R3 c 0 R=75\n" + Block("a", "b", "c") +
           $"analysis F1 type=fields block=EM1 {fields} drive=1\n" + sweep;

    /// <summary>The cube's values at one point of its first axis, as a single run's buffer is laid out.</summary>
    private static void SamePoint(DataCube swept, int point, DataCube single, string name)
    {
        Assert.Equal(single.Axes.Select(a => a.Name), swept.Axes.Skip(1).Select(a => a.Name));
        int n = single.BufferLength;
        if (single.DataKind == DataKind.Complex)
            for (int i = 0; i < n; i++)
                Assert.True((swept.ComplexValues[point * n + i] - single.ComplexValues[i]).Magnitude <= 1e-12 * Math.Max(1, single.ComplexValues[i].Magnitude), $"{name}[{i}]");
        else
            for (int i = 0; i < n; i++)
                Assert.True(Math.Abs(swept.RealValues[point * n + i] - single.RealValues[i]) <= 1e-12 * Math.Max(1, Math.Abs(single.RealValues[i])), $"{name}[{i}]");
    }

    [Theory]
    [InlineData("freq=1 freqUnit=GHz")]
    [InlineData("freq=all")]
    public void ASweepsPoint_IsASingleRunAtThatValue_CubeForCube(string frequency)
    {
        WriteResult();
        var swept = Run(Swept(frequency)).Results.Single().Data;
        Assert.Equal(["Lv", "freq", "theta", "phi", "port"], swept["farfield.U"].Axes.Select(a => a.Name));
        Assert.Equal("H", swept["farfield.U"].Axes[0].Unit);
        for (int p = 0; p < 3; p++)
        {
            var single = Run(Swept(frequency, "", $"Lv = {1 + 4 * p} nH")).Results.Single().Data;
            Assert.Equal(single.Cubes.Keys.Order(), swept.Cubes.Keys.Order());
            foreach (var (name, cube) in single.Cubes) SamePoint(swept[name], p, cube, name);
        }
    }

    // R-cp5-1 / R-cp5-2 — naming the analysis inside runs the whole chain, a nested sweep adds an axis, and the plan counts every point.
    [Fact]
    public void ANamedInner_IsPromotedToItsSweeps_AndANestedSweepAddsAnAxis()
    {
        WriteResult();
        var circuit = PreparedCircuit.FromText("Rv = 75\n" + Swept("freq=1 freqUnit=GHz",
            "analysis SW1 type=parametric_sweep Var=Lv Start=1 Stop=9 Step=4 Unit=nH Inner=F1\nanalysis SW2 type=parametric_sweep Var=Rv Values=50,75 Inner=SW1\n"), _dir, _dir);
        var request = new CircuitEvaluationRequest { Analyses = ["F1"] };
        var plan = CircuitEvaluation.Plan(circuit, request);
        Assert.Equal(6, plan.SweepPoints);
        Assert.Contains("6 points", plan.Lines.Single());
        var run = CircuitEvaluation.Execute(plan);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.Equal(["Rv", "Lv", "freq", "theta", "phi", "port"], run.Results.Single().Data["farfield.U"].Axes.Select(a => a.Name));
    }

    // A field set beside the far field stops a sweep only where the analysis would run it, and writes nothing where it does not.
    [Theory]
    [InlineData("freq=all", 2)]
    [InlineData("freq=240 freqUnit=MHz", 1)]
    public void ARecordWithBoth_Sweeps_WhereTheAnalysisTakesTheFarFieldAlone(string frequency, int frequencies)
    {
        WriteResultWithBoth();
        string Bench(string fields) => $"Cv = 2 pF\nPort:P1 in 0 Num=1 Z=50 Ohm\nC:C1 p2 0 C=Cv\n{Block("in", "p2")}analysis F1 type=fields block=EM1 {fields} drive=1 average=no\n" +
                                       "analysis SW1 type=parametric_sweep Var=Cv Start=1 Stop=3 Step=1 Unit=pF Inner=F1\n";
        var data = Run(Bench(frequency)).Results.Single().Data;
        var u = data["farfield.U"];
        Assert.Equal((3, frequencies), (u.Axes[0].Length, u.Axes[1].Length));
        Assert.False(data.Cubes.ContainsKey(FieldCosimulation.PeakLocalCube));
        Assert.False(Directory.Exists(Path.Combine(_dir, "ems.fields", FieldCosimulation.CosimFolder)));

        var refused = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(Bench("freq=300 freqUnit=MHz"), _dir, _dir));
        Assert.Equal(RunStatus.EngineError, refused.Status);
        Assert.Contains("use freq=all", refused.StatusMessage);
    }

    // R-cp5-2 — a field set is refused, whose sentence says why and is the same before the run and at it; with no far field there is no freq=all to offer.
    [Fact]
    public void ABlockWithAFieldSet_IsRefused_NamingTheLimit()
    {
        new MemoryFieldSet().WriteBeside(_dir, F0, "stamp-1");
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(
            $"Lv = 5 nH\nPort:P1 a 0 Num=1 Z=50 Ohm\nL:L1 b 0 L=Lv\n{Block("a", "b")}analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n" +
            "analysis SW1 type=parametric_sweep Var=Lv Start=1 Stop=9 Step=4 Unit=nH Inner=F1\n", _dir, _dir));
        Assert.Equal(RunStatus.EngineError, run.Status);
        Assert.Contains("a parametric sweep wraps a far field alone", run.StatusMessage);
        Assert.Contains("holds a field set", run.StatusMessage);
        Assert.Contains("ems.cemr", run.StatusMessage);
        Assert.DoesNotContain("freq=all", run.StatusMessage);
    }

    // R-cp5-1 — with nothing to run it, the engine refuses a fields analysis in the words it always has.
    [Fact]
    public void ASweepWithNoInnerRunner_IsRefusedAsEver()
    {
        WriteResult();
        var circuit = PreparedCircuit.FromText(Swept("freq=1 freqUnit=GHz"), _dir, _dir);
        var tb = circuit.Tb!;
        var refused = Assert.Throws<NotSupportedException>(() =>
            CircuitRF.Engine.ParametricSweepEngine.Run(tb.Analyses.OfType<CircuitRF.Core.Design.ParametricSweepAnalysis>().Single(), circuit.Lib!, tb, baseDirectory: _dir));
        Assert.Contains("inner analysis type 'FieldsAnalysis' is not supported", refused.Message);
    }

    // R-cp5-3 — a value that moves freq= fails its own point: NaN, a warning naming the value, the others kept and the cube's units with them.
    [Fact]
    public void AFailedPoint_IsNaN_NamingItsValue_AndTheOthersAreKept()
    {
        WriteResult();
        var run = Run(Swept("freq=Fv freqUnit=GHz", "analysis SW1 type=parametric_sweep Var=Fv Values=1,2 Inner=F1\n", "Lv = 5 nH\nFv = 1"));
        var u = run.Results.Single().Data["farfield.U"];
        int n = Grid.DirectionCount;
        Assert.DoesNotContain(u.RealValues[..n], double.IsNaN);
        Assert.All(u.RealValues[n..], v => Assert.True(double.IsNaN(v)));
        Assert.Equal("W/sr", u.Unit);
        Assert.Contains(run.Warnings, w => w.Contains("Fv = 2") && w.Contains("that point is NaN") && w.Contains("freq=all"));
    }

    // R-cp5-4 — the size is said before the run, its figure is what the cubes take, and past 2 GB the warning names the remedy.
    [Fact]
    public void ItsSize_IsStatedBeforeTheRun_AndALargeOneIsWarnedAbout()
    {
        WriteResult();
        var plan = CircuitEvaluation.Plan(PreparedCircuit.FromText(Swept("freq=all"), _dir, _dir), new CircuitEvaluationRequest());
        Assert.Contains($"{Grid.DirectionCount} directions x 2 frequencies x 3 points", plan.Lines.Single());

        var data = Run(Swept("freq=1 freqUnit=GHz", "")).Results.Single().Data;
        // Every cube the size of the grid: 56 bytes on this fixture, whose random field names no polarization to reference, and the 72 stated once the Ludwig-3 pair is there.
        double taken = data.Cubes.Values.Where(c => c.BufferLength >= Grid.DirectionCount)
                           .Sum(c => (double)c.BufferLength * (c.DataKind == DataKind.Complex ? 16 : 8)) / Grid.DirectionCount;
        Assert.InRange(taken, 56, FieldsSweepSize.BytesPerDirection);

        Assert.Equal("4.7 MB", new FieldsSweepSize(65160, 1, 1).Amount);
        var every = new CircuitRF.Core.Design.FieldsAnalysis("F1") { FreqExpr = "all" };
        Assert.Null(FieldCosimulation.SweepSizeWarning(every, new FieldsSweepSize(65160, 21, 20)));
        Assert.Contains("freq=", FieldCosimulation.SweepSizeWarning(every, new FieldsSweepSize(65160, 21, 30)));
    }

    // A scalar function has one number per point under a sweep, and it says where those numbers are.
    [Fact]
    public void APatternFunction_UnderASweep_NamesTheCubeThatHoldsIt()
    {
        WriteResult();
        var run = Run(Swept("freq=1 freqUnit=GHz") + "measure G = RealizedGainDbi(F1)\n", measurements: true);
        Assert.Contains("F1.farfield.RealizedGainDbi over Lv", run.StatusMessage);
    }

    // R-cp5-5 — a cut over the sweep: distinct curves with their values, in the legend, and a whole-plane cut is the plane per curve.
    [Fact]
    public void ACutOverASweep_IsOneLabelledCurvePerValue()
    {
        WriteResult();
        var data = Run(Swept("freq=1 freqUnit=GHz")).Results.Single().Data;
        Assert.True(CubeTraceSpecParser.TryParse("db10(farfield.U[~, 0, :, 0, 1])", data, out string cube, out var slice, out var transform, out string error), error);
        Trace Resolved(bool wholePlane)
        {
            var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
            {
                CubeName = cube, Slice = slice, Transform = transform, PatternWholePlane = wholePlane,
                PatternScale = new PolarPatternScale { ReferenceDb = 0, FloorDb = -200, RingStepDb = 10, Normalised = false, Unit = "dB" },
            };
            TraceResolve.SetCubeDataFrom(t, data, PlotType.Polar, FreqUnit.GHz);
            return t;
        }
        var front = Resolved(false);
        Assert.True(front.LabelsFamilyCurves);
        Assert.Equal(["Lv = 1 nH", "Lv = 5 nH", "Lv = 9 nH"], Enumerable.Range(0, 3).Select(front.FamilyCurveLabel));
        Assert.Equal(3, Enumerable.Range(0, 3).Select(front.FamilyCurveColor).Distinct().Count());

        var plot = new Plot(PlotType.Polar, FreqUnit.GHz) { Legend = LegendPlacement.Inside };
        plot.Traces.Add(front);
        var mirrored = Resolved(false);
        mirrored.MirrorPatternAngle = true;
        plot.Traces.Add(mirrored);
        Assert.Equal(["Lv = 1 nH", "Lv = 5 nH", "Lv = 9 nH"], LegendRenderer.Entries(plot, false, null).Select(e => e.Label));

        var whole = Resolved(true);
        int theta = data["farfield.U"].Axes[2].Length;
        Assert.All(whole.FamilyCurves, c => Assert.Equal(2 * theta, c.Points.Count));
    }
}
