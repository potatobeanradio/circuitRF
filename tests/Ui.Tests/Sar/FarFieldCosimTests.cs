// ================================================================
//  FarFieldCosimTests.cs — brief-cosim-pattern-3's routine gates, on a small far field built here with no solver: the pattern of a whole
//  circuit from a result's per-port patterns, the circuit's S beside it, and what it refuses.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class FarFieldCosimTests : IDisposable
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

    [Fact]
    public void ARecordWithBoth_GivesBothInOneAnalysis_AndSaysWhichHalfAFrequencyLacks()
    {
        const double Sar = 300e6;
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
}
