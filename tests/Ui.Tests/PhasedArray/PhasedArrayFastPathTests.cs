// brief-paa-8 — the identical-channel fast path of a Feed-mode Phased Antenna Array: gates 1-3, and the estimate a slider's drag shows where
// the fast path does not apply (R-paa8-4). Counters, not timing (gate 4's numbers are in src/Engine/RESOLVED.md). Fixtures: testdata/phased-array.

using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine.AntennaArray;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayFastPathTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa8-").FullName;

    public void Dispose()
    {
        PhasedArrayFastPath.Enabled = true;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static readonly string E5 = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";

    private const string Channel = """
        define chan ( in out )
        Amp:A1 in 0 mid 0 Gain=20 IP3=200
        Atten:T1 mid 0 out 0 Loss=LossDb
        C:C1 out 0 C=0.3pF
        end
        """;

    private static string Bench(int side, string extra, string loss = "lossDb")
        => Channel.Replace("LossDb", loss) + "\nPort:P1 f 0 Num=1 Z=50 Ohm\ntheta0 = 20 deg\nf0 = 5.9 GHz\nlossDb = 3\n" +
           $"PhasedArray:PA1 f {E5} Mode=feed Rows={side} Cols={side} Phase=steer(theta0,0,f0) Amp=cosq(1) Channel=chan {extra}\n" +
           "analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1\n";

    private RunResult Run(PreparedCircuit circuit, Dictionary<string, string>? tunables = null, FieldsOutput fields = FieldsOutput.Written)
    {
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Tunables = tunables, Fields = fields });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    private static DataSet Data(RunResult run) => run.Results.Single(r => r.Name == "F1").Data;

    private void Same(DataSet want, DataSet got, string cube, double tol)
    {
        var a = want.Cubes[cube].ComplexValues;
        var b = got.Cubes[cube].ComplexValues;
        Assert.Equal(a.Length, b.Length);
        // S is at most 1 and here near zero (the channel's amplifier is unilateral, so F sees no reflection): it is compared absolutely
        double peak = cube == "S" ? 1 : a.Where(c => !double.IsNaN(c.Real)).Max(c => c.Magnitude), worst = 0;
        for (int i = 0; i < a.Length; i++)
            if (!double.IsNaN(a[i].Real)) worst = Math.Max(worst, (a[i] - b[i]).Magnitude / peak);
        output.WriteLine($"{cube}: worst difference {worst:E2} of the peak");
        Assert.True(worst <= tol, $"{cube}: worst difference {worst:E2} of the peak");
    }

    /// <summary>Gate 1 — at 4096 elements with an identical channel, ten evaluations moving only theta0 make one channel solve per frequency, one
    /// closure factorisation and ten back-substitutions; moving the channel's own VAR once adds one solve and one factorisation.</summary>
    [Fact]
    public void Gate1_OnlyTheWeightsMoving_SolvesTheChannelAndFactorisesOnce()
    {
        PhasedArrayFastPath.Clear();
        var circuit = PreparedCircuit.FromText(Bench(64, ""), _dir, _dir);
        long solves = PhasedArrayFastPath.ChannelSolves, factors = PhasedArrayFastPath.ClosureFactorisations, backs = PhasedArrayFastPath.BackSubstitutions;
        for (int i = 0; i < 10; i++) Run(circuit, new() { ["theta0"] = $"{3 * i} deg" });
        output.WriteLine($"ten moves of theta0: {PhasedArrayFastPath.ChannelSolves - solves} channel solves, {PhasedArrayFastPath.ClosureFactorisations - factors} " +
                         $"factorisations, {PhasedArrayFastPath.BackSubstitutions - backs} back-substitutions; {PhasedArrayFastPath.LastTimings}");
        Assert.Equal(1, PhasedArrayFastPath.ChannelSolves - solves);
        Assert.Equal(1, PhasedArrayFastPath.ClosureFactorisations - factors);
        Assert.Equal(10, PhasedArrayFastPath.BackSubstitutions - backs);

        Run(circuit, new() { ["theta0"] = "27 deg", ["lossDb"] = "4" });
        Assert.Equal(2, PhasedArrayFastPath.ChannelSolves - solves);
        Assert.Equal(2, PhasedArrayFastPath.ClosureFactorisations - factors);
    }

    /// <summary>Gate 2 — the fast path's pattern and active reflections are the full solve's to 1e-10, coupled and not, at 256 elements; coupled
    /// with three elements switched off, the update against the kept factor included.</summary>
    [Theory]
    [InlineData("Coupling=on")]
    [InlineData("Coupling=off")]
    [InlineData("Coupling=on Off=6,100,256")]
    public void Gate2_TheFastPath_IsTheFullSolve(string extra) => FastEqualsFull(16, extra);

    /// <summary>Gate 2 at 4096 elements, coupled: the full solve it is compared with takes seconds.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate2_TheFastPath_IsTheFullSolve_At4096() => FastEqualsFull(64, "Coupling=on");

    private void FastEqualsFull(int side, string extra)
    {
        var circuit = PreparedCircuit.FromText(Bench(side, extra), _dir, _dir);
        long fast = PhasedArrayFastPath.FastEvaluations;
        var quick = Data(Run(circuit));
        Assert.Equal(fast + 1, PhasedArrayFastPath.FastEvaluations);
        PhasedArrayFastPath.Enabled = false;
        var full = Data(Run(circuit));
        PhasedArrayFastPath.Enabled = true;
        Assert.Equal(fast + 1, PhasedArrayFastPath.FastEvaluations);
        foreach (string cube in (string[])["farfield.Etheta", "farfield.Ephi", "array.GammaActive", "array.a", "S"]) Same(full, quick, cube, 1e-10);
    }

    /// <summary>Gate 3 — a channel whose attenuator reads n takes the full solve, and explain names the parameter that made it differ.</summary>
    [Fact]
    public void Gate3_AChannelReadingTheElement_FallsBack_AndExplainNamesIt()
    {
        string cnl = Path.Combine(_dir, "differs.cnl");
        File.WriteAllText(cnl, Bench(4, "", "3+0.1*n"));
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "circuitRF.slnx"))) root = Path.GetDirectoryName(root)!;
        var (code, stdout, stderr) = CircuitRF.Ui.Tests.Em3d.CliProcess.Run(root, [], "explain", cnl, "--analysis", "F1");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, code);
        Assert.Contains("Full solve: channel cell 'chan' differs per element: it reads n, in T1.Loss=3+0.1*n", stdout);

        long fast = PhasedArrayFastPath.FastEvaluations;
        Run(PreparedCircuit.FromText(Bench(4, "", "3+0.1*n"), _dir, _dir));
        Assert.Equal(fast, PhasedArrayFastPath.FastEvaluations);
    }

    /// <summary>R-paa8-4 — where the fast path does not apply, a drag (local output) shows the last full solve with the new weights, labelled
    /// estimated, and the release (written output) solves: its pattern is the full solve's, not the estimate's.</summary>
    [Fact]
    public void ADrag_OffTheFastPath_IsEstimated_AndTheReleaseSolves()
    {
        var circuit = PreparedCircuit.FromText(Bench(4, "", "3+0.1*n"), _dir, _dir);
        Run(circuit);
        var drag = Run(circuit, new() { ["theta0"] = "35 deg" }, FieldsOutput.Local);
        Assert.Contains(drag.Notes, s => s.Contains(PhasedArrayFastPath.EstimatedSentence, StringComparison.Ordinal));
        var release = Run(circuit, new() { ["theta0"] = "35 deg" }, FieldsOutput.Written);
        Assert.DoesNotContain(release.Notes, s => s.Contains(PhasedArrayFastPath.EstimatedSentence, StringComparison.Ordinal));
        var a = Data(drag).Cubes["array.a"].ComplexValues;
        var b = Data(release).Cubes["array.a"].ComplexValues;
        double moved = a.Zip(b).Max(p => (p.First - p.Second).Magnitude) / b.Max(c => c.Magnitude);
        output.WriteLine($"estimate against the solve: {moved:E2} of the largest wave");
        Assert.True(moved > 0, "the estimate held the circuit, so it is not the solve exactly");
    }
}
