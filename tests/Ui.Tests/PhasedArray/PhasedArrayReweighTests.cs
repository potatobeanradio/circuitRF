// brief-paa-8b — a tuning move that reaches only a Feed-mode array's weights re-weighs the kept netlist instead of elaborating: gates 1-3
// (counters, not timing; gate 4's numbers are in src/Core/RESOLVED.md). Fixtures: testdata/phased-array.

using System.Runtime.CompilerServices;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine.AntennaArray;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayReweighTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa8b-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static readonly string E5 = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";

    private static string Bench(int side, string extra = "", string fields = "freq=5.9", string loss = "lossDb")
        => "define chan ( in out )\nAmp:A1 in 0 mid 0 Gain=20 IP3=200\nAtten:T1 mid 0 out 0 Loss=" + loss + "\nC:C1 out 0 C=0.3pF\nend\n" +
           "Port:P1 f 0 Num=1 Z=50 Ohm\ntheta0 = 20 deg\nphi0 = 0 deg\nf0 = 5.9 GHz\nlossDb = 3\nbits = 0\nfq = 5.9\n" +
           $"PhasedArray:PA1 f {E5} Mode=feed Rows={side} Cols={side} Phase=steer(theta0,phi0,f0) Amp=cosq(1) Channel=chan {extra}\n" +
           $"analysis F1 type=fields block=PA1 {fields} freqUnit=GHz drive=1\nmeasure pe = PointingErrorDeg(F1, theta0, 0)\n";

    private static RunResult Run(PreparedCircuit circuit, Dictionary<string, string> tunables, params string[] expressions)
    {
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Tunables = tunables, Fields = FieldsOutput.Local, Expressions = expressions });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    private static DataSet Data(RunResult run) => run.Results.Single(r => r.Name == "F1").Data;

    /// <summary>Gate 1 — ten moves of theta0 at 4096 elements make one elaboration and nine re-weighs (and PAA-8's one factorisation and ten
    /// back-substitutions); the channel's VAR elaborates.</summary>
    [Fact]
    public void Gate1_TenMovesOfTheta0_ElaborateOnce()
    {
        PhasedArrayFastPath.Clear();
        var circuit = PreparedCircuit.FromText(Bench(64), _dir, _dir);
        long factors = PhasedArrayFastPath.ClosureFactorisations, backs = PhasedArrayFastPath.BackSubstitutions;
        for (int i = 0; i < 10; i++) Run(circuit, new() { ["theta0"] = $"{3 * i} deg" });
        output.WriteLine($"ten moves: {circuit.Elaborations} elaborations, {circuit.Reweighs} re-weighs, " +
                         $"{PhasedArrayFastPath.ClosureFactorisations - factors} factorisations, {PhasedArrayFastPath.BackSubstitutions - backs} back-substitutions");
        Assert.Equal(1, circuit.Elaborations);
        Assert.Equal(9, circuit.Reweighs);
        Assert.Equal(1, PhasedArrayFastPath.ClosureFactorisations - factors);
        Assert.Equal(10, PhasedArrayFastPath.BackSubstitutions - backs);

        Run(circuit, new() { ["theta0"] = "27 deg", ["lossDb"] = "4" });
        Assert.Equal(2, circuit.Elaborations);
        Assert.Equal(9, circuit.Reweighs);
    }

    /// <summary>Gate 1's other half — a VAR read by a cell's instance (the channel's attenuator), by PhaseBits or by the analysis's frequency
    /// elaborates at every move.</summary>
    [Theory]
    [InlineData("lossDb", "4", "", "freq=5.9")]
    [InlineData("bits", "3", "PhaseBits=bits", "freq=5.9")]
    [InlineData("fq", "5.9000000001", "", "freq=fq")]
    public void AValueReadElsewhere_Elaborates(string name, string value, string extra, string fields)
    {
        var circuit = PreparedCircuit.FromText(Bench(4, extra, fields), _dir, _dir);
        Run(circuit, new() { [name] = name == "bits" ? "0" : name == "fq" ? "5.9" : "3" });
        Run(circuit, new() { [name] = value });
        Assert.Equal(2, circuit.Elaborations);
        Assert.Equal(0, circuit.Reweighs);
    }

    /// <summary>Gate 2 — at 256 elements, coupled with an identical channel, a re-weighed evaluation's pattern, Γ_act, waves and the measurement
    /// reading theta0 are a full elaboration's to 1e-12, and its notes are the same.</summary>
    [Fact]
    public void Gate2_TheReweigh_IsTheElaboration()
    {
        var kept = PreparedCircuit.FromText(Bench(16, "Off=7,200"), _dir, _dir);
        Run(kept, new() { ["theta0"] = "10 deg" });
        var reweighed = Run(kept, new() { ["theta0"] = "33 deg" }, "PointingErrorDeg(F1, theta0, 0)");
        Assert.Equal(1, kept.Reweighs);
        var fresh = PreparedCircuit.FromText(Bench(16, "Off=7,200"), _dir, _dir);
        var full = Run(fresh, new() { ["theta0"] = "33 deg" }, "PointingErrorDeg(F1, theta0, 0)");
        Assert.Equal(0, fresh.Reweighs);

        foreach (string cube in (string[])["farfield.Etheta", "farfield.Ephi", "array.GammaActive", "array.a", "array.wQuantised"])
        {
            var a = Data(full).Cubes[cube].ComplexValues;
            var b = Data(reweighed).Cubes[cube].ComplexValues;
            double peak = a.Where(c => !double.IsNaN(c.Real)).Max(c => c.Magnitude), worst = 0;
            for (int i = 0; i < a.Length; i++) if (!double.IsNaN(a[i].Real)) worst = Math.Max(worst, (a[i] - b[i]).Magnitude / peak);
            output.WriteLine($"{cube}: {worst:E2} of the peak");
            Assert.True(worst <= 1e-12, $"{cube}: {worst:E2}");
        }
        double pf = full.Expressions[0].Value!.Value.AsReal(), pr = reweighed.Expressions[0].Value!.Value.AsReal();
        output.WriteLine($"pointing error {pf:R} full, {pr:R} re-weighed");
        Assert.True(Math.Abs(pf - pr) <= 1e-12, $"{pf} against {pr}");
        Assert.Equal(full.Notes, reweighed.Notes);
        Assert.Equal(full.Warnings, reweighed.Warnings);
    }

    /// <summary>Gate 3 — twenty-five steering directions at 4096 elements, one after another, make one elaboration.</summary>
    [Fact]
    public void Gate3_AScanOfTwentyFiveDirections_ElaboratesOnce()
    {
        var circuit = PreparedCircuit.FromText(Bench(64), _dir, _dir);
        foreach (int t in new[] { 0, 10, 20, 30, 40 })
            foreach (int p in new[] { 0, 45, 90, 135, 180 })
                Run(circuit, new() { ["theta0"] = $"{t} deg", ["phi0"] = $"{p} deg" });
        output.WriteLine($"{circuit.Elaborations} elaboration, {circuit.Reweighs} re-weighs");
        Assert.Equal(1, circuit.Elaborations);
        Assert.Equal(24, circuit.Reweighs);
    }
}
