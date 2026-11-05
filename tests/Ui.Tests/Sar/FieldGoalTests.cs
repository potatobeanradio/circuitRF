// ================================================================
//  FieldGoalTests.cs — the gates of brief-sar-10: B1 and SAR as measurements, goals, optimizer and yield inputs, with no
//  openEMS anywhere.
//
//    FieldGoalTests          every field measurement on a synthetic two-port field set against its hand value, as Simulate
//                            and as an optimizer evaluation (which recombines nothing until asked); a region by tissue and by
//                            box; the refusals; and two circuit ports driven together (R-sar10-4)
//    LoopCoilOptimizeTests   from 4.5 pF the optimizer matches the loop coil (S11 below −15 dB at 298 MHz) on its fixed .s4p;
//                            where this machine still holds the coil's field set, B1pMean there is the SAR-9 fixed data's to 2 %
//    CapacitorYieldTests     200 trials of a 2 % capacitor on the pair's fixed field set, psSAR1g a yield spec, no openEMS
//                            launch; its spread reported; the worst trial re-run writes its field for the 3D view
// ================================================================

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine.Em3d.Sar;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class FieldGoalTests(ITestOutputHelper output) : IDisposable
{
    private const double Hz = 300e6;
    private const string Box = "box(-10mm,-2mm,0mm,6mm,20mm,32mm)";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-sar10-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Bench(string dir, string drive = "drive=1", string ports = "Port:P1 in 0 Num=1 Z=50 Ohm\nC:C1 p2 0 C=2e-12") => $"""
        W2 = polar(1,-90)
        {ports}
        EmFieldData:EM1 in p2 File="{Path.Combine(dir, "ems.cemr")}"
        analysis F1 type=fields block=EM1 freq=300 freqUnit=MHz {drive} average=no
        """;

    private static readonly string[] Expressions =
    [
        "B1pMean(F1)", "B1pMean(F1, \"block\")", "B1pMin(F1, \"block\")", "B1pCoV(F1, \"block\")", "SARwb(F1, \"block\")",
        "Pabs(F1)", "psSAR(F1, 1)", "SarEff(F1, \"all\", 1)", $"B1pMean(F1, \"{Box}\")", $"SARwb(F1, \"{Box}\")",
    ];

    private (PreparedCircuit Circuit, MemoryFieldSet Set) Prepare(string drive = "drive=1", string? ports = null)
    {
        var set = new MemoryFieldSet();
        set.WriteBeside(_dir, Hz, "stamp-1");
        return (ports is null ? PreparedCircuit.FromText(Bench(_dir, drive), _dir, _dir)
                              : PreparedCircuit.FromText(Bench(_dir, drive, ports), _dir, _dir), set);
    }

    private static RunResult Run(PreparedCircuit circuit, FieldsOutput mode, params string[] expressions)
    {
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Expressions = expressions, Fields = mode });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    /// <summary>The hand values, from the set's own float32 fields summed with the run's per-watt waves.</summary>
    private static double[] ByHand(MemoryFieldSet set, Complex[] a)
    {
        int n = set.Count;
        var ex = new Complex[n]; var ey = new Complex[n]; var ez = new Complex[n];
        var b1 = new double[n];
        for (int c = 0; c < n; c++)
        {
            Complex Sum(Complex[][] f, int comp) => a[0] * Cast(f[0][3 * c + comp]) + a[1] * Cast(f[1][3 * c + comp]);
            ex[c] = Sum(set.E, 0); ey[c] = Sum(set.E, 1); ez[c] = Sum(set.E, 2);
            b1[c] = SarB1.FromH(Sum(set.H, 0), Sum(set.H, 1), Sum(set.H, 2), 2, 1).Plus.Magnitude;
        }
        (double Mean, double Min, double CoV) B1Stats(Func<int, bool> keep)
        {
            double v = 0, s = 0, s2 = 0, min = double.MaxValue;
            for (int c = 0; c < n; c++)
                if (keep(c)) { v += set.Volume[c]; s += b1[c] * set.Volume[c]; s2 += b1[c] * b1[c] * set.Volume[c]; min = Math.Min(min, b1[c]); }
            double mean = s / v;
            return (mean, min, Math.Sqrt(s2 / v - mean * mean) / mean);
        }
        (double Power, double Mass) Absorbed(Func<int, bool> keep)
        {
            double p = 0, m = 0;
            for (int c = 0; c < n; c++)
                if (keep(c) && set.Rho[c] > 0)
                {
                    double e2 = ex[c].Magnitude * ex[c].Magnitude + ey[c].Magnitude * ey[c].Magnitude + ez[c].Magnitude * ez[c].Magnitude;
                    p += 0.5 * set.Sigma[c] * e2 * set.Volume[c];
                    m += (double)set.Rho[c] * set.Volume[c];
                }
            return (p, m);
        }
        bool Tissue(int c) => set.Rho[c] > 0;
        bool InBox(int c)
        {
            int i = c / (set.Y.Count * set.Z.Count), j = c / set.Z.Count % set.Y.Count, k = c % set.Z.Count;
            return i <= 1 && j <= 1 && k <= 1;      // the box's corners hold the centres of cells 0 and 1 on each axis
        }
        var grid = SarGrid.FromCells(set.X, set.Y, set.Z, ex, ey, ez, set.Sigma, set.Rho, set.Volume);
        double ps = SarEngine.Compute(grid, new SarOptions { MassesGrams = [1], Method = SarMethod.Cube }).Averages[0].Peak!.Value;
        var tissue = B1Stats(Tissue);
        var (pw, mw) = Absorbed(Tissue);
        var (pb, mb) = Absorbed(InBox);
        return
        [
            tissue.Mean, tissue.Mean, tissue.Min, tissue.CoV, pw / mw,
            pw, ps, tissue.Mean / Math.Sqrt(ps), B1Stats(InBox).Mean, pb / mb,
        ];
    }

    private static Complex[] Waves(RunResult run) => run.Results.Single().Data.Cubes[FieldCosimulation.IncidentCube].ComplexValues.ToArray();

    [Fact]
    public void EachFieldMeasurementIsItsHandValue_AsSimulateAndAsAnOptimizerEvaluation()
    {
        var (circuit, set) = Prepare();
        long averages = CosimFieldMetrics.FullAverages;
        foreach (var mode in new[] { FieldsOutput.Written, FieldsOutput.OnDemand })
        {
            var run = Run(circuit, mode, Expressions);
            var hand = ByHand(set, Waves(run));
            for (int i = 0; i < Expressions.Length; i++)
            {
                var o = run.Expressions[i];
                Assert.True(o.Error is null, $"{mode} {o.Expression}: {o.Error}");
                double v = o.Value!.Value.AsReal();
                output.WriteLine($"{mode,-9} {o.Expression,-48} {v,14:G7}  by hand {hand[i],14:G7}");
                Assert.Equal(hand[i], v, Math.Abs(hand[i]) * 2e-5);
            }
            // Simulate's own scalars are the same measurements over the setup's own region
            if (mode == FieldsOutput.Written)
            {
                var data = run.Results.Single().Data;
                Assert.Equal(data.Cubes[OpenEmsSarResults.B1MeanCube].RealValues[0], run.Expressions[0].Value!.Value.AsReal(), hand[0] * 1e-6);
            }
        }
        // the on-demand run averaged once more for psSAR and SarEff together (the second reads the first); Written averaged it on
        // its grid too, since its analysis says average=no
        Assert.Equal(averages + 2, CosimFieldMetrics.FullAverages);
    }

    [Fact]
    public void AnOptimizerEvaluationWritesNothing_AndAMovingKnobDefersTheAverage()
    {
        var (circuit, _) = Prepare();
        string cosim = Path.Combine(PortFieldSet.FolderFor(Path.Combine(_dir, "ems.s2p")), FieldCosimulation.CosimFolder);
        Run(circuit, FieldsOutput.OnDemand, "Pabs(F1)");
        Assert.False(Directory.Exists(cosim), "an on-demand evaluation wrote a recombination");

        var local = Run(circuit, FieldsOutput.Local, "psSAR(F1, 1)", "B1pMean(F1)");
        Assert.Contains("released", local.Expressions[0].Error);
        Assert.Null(local.Expressions[1].Error);
        Assert.True(File.Exists(Path.Combine(cosim, "F1", SarVolumeFiles.RecordFile(0))), "a moving knob wrote no local SAR and B1 for the 3D view");

        var refused = Run(circuit, FieldsOutput.OnDemand, "B1pMean(F1, \"brain\")", "psSAR(F1, 0)", "B1pMean(SP9)");
        Assert.Contains("no tissue is called 'brain'", refused.Expressions[0].Error);
        Assert.Contains("'block'", refused.Expressions[0].Error);
        Assert.Contains("positive mass", refused.Expressions[1].Error);
        Assert.Contains("No fields analysis named 'SP9'", refused.Expressions[2].Error);
    }

    [Fact]
    public void TwoCircuitPortsDriveTogether_WithTheirWeights()
    {
        // each circuit port sits on one block port, so the block's incident waves are the drives' own: a2/a1 is W2
        var (circuit, set) = Prepare("drive=1,2 weights=1,W2", "Port:P1 in 0 Num=1 Z=50 Ohm\nPort:P2 p2 0 Num=2 Z=50 Ohm");
        var run = Run(circuit, FieldsOutput.OnDemand, "B1pMean(F1)", "Pabs(F1)");
        var a = Waves(run);
        var ratio = a[1] / a[0];
        output.WriteLine($"a2/a1 = {ratio.Real:F6}{ratio.Imaginary:+0.000000;-0.000000}j");
        Assert.Equal(0, ratio.Real, 1e-9);
        Assert.Equal(-1, ratio.Imaginary, 1e-9);
        var hand = ByHand(set, a);
        Assert.Equal(hand[0], run.Expressions[0].Value!.Value.AsReal(), hand[0] * 2e-5);
        Assert.Equal(hand[5], run.Expressions[1].Value!.Value.AsReal(), hand[5] * 2e-5);
    }

    private static Complex Cast(Complex c) => new((float)c.Real, (float)c.Imaginary);
}

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class LoopCoilOptimizeTests(ITestOutputHelper output)
{
    private static string Bench(string snp, bool fields) => $"""
        Cgap = 4.5 pF
        Port:P1 feed 0 Num=1 Z=2.5 Ohm
        {(fields ? $"EmFieldData:EM1 feed cl cr ct File=\"{snp}\"" : $"SnP:EM1 feed cl cr ct 0 NumPorts=4 File=\"{snp}\"")}
        C:Cleft cl 0 C=Cgap
        C:Cright cr 0 C=Cgap
        C:Ctop ct 0 C=Cgap
        analysis SP1 type=sparam start=297 stop=299 npts=3 Unit=MHz
        {(fields ? "analysis F1 type=fields block=EM1 freq=298 freqUnit=MHz drive=1" : "")}
        tune Cgap min=3 pF max=8 pF opt=1
        goal Match = dB(SP1.S(1,1)) analysis=SP1 over=freq lo=298 MHz hi=298 MHz le -15
        optimize algorithm=simplex seed=1
        """;

    /// <summary>The optimizer's capacitor, from 4.5 pF, on the coil's committed .s4p; and S11 at 298 MHz there.</summary>
    private (string Cgap, double S11dB) Optimize()
    {
        long launches = OpenEmsRun.Invocations;
        var circuit = PreparedCircuit.FromText(Bench(Path.Combine(LoopCosim.Output, "LoopPorts.s4p"), fields: false), LoopCosim.Folder, LoopCosim.Folder);
        var result = OptimizationRun.Create(circuit, new OptimizationOptions()).Run();
        Assert.Equal(OptimizationOutcome.GoalsMet, result.Outcome);
        string cgap = result.BestValues["Cgap"];
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Tunables = result.BestValues, Expressions = ["dB(SP1.S(1,1))"] });
        double s11 = run.Expressions[0].Value!.Value.AsCube().RealValues[1];
        output.WriteLine($"Cgap {cgap} after {result.Evaluations} evaluations: S11 {s11:F2} dB at 298 MHz");
        Assert.Equal(launches, OpenEmsRun.Invocations);
        return (cgap, s11);
    }

    [CosimLoopFact]
    public void FromFourPointFivePicofarads_TheOptimizerMatchesTheCoil()
    {
        var (_, s11) = Optimize();
        Assert.True(s11 < -15, $"S11 {s11:F2} dB at 298 MHz");
    }

    [CosimLoopFieldSetFact]
    public void AtTheOptimum_B1pMeanIsTheFixedDatas()
    {
        var (cgap, _) = Optimize();
        var circuit = PreparedCircuit.FromText(Bench(LoopCosim.Record(LoopCosim.Workspace), fields: true), LoopCosim.Folder, LoopCosim.Folder);
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
        {
            Tunables = new Dictionary<string, string> { ["Cgap"] = cgap }, Analyses = ["F1"], Fields = FieldsOutput.OnDemand,
            Expressions = ["B1pMean(F1, \"brain\")"],
        });
        Assert.True(run.Expressions[0].Error is null, run.Expressions[0].Error);
        double b1 = run.Expressions[0].Value!.Value.AsReal();
        double kept = SarVolumeFiles.ReadRecord(Path.Combine(LoopCosim.Output, LoopReplication.Record))!.B1pMeanPerSqrtWatt!.Value;
        output.WriteLine($"mean |B1+| over the brain {b1 * 1e6:F4} µT/√W at {cgap}, the fixed data's (5.4 pF) {kept * 1e6:F4} ({100 * (b1 / kept - 1):+0.00;-0.00} %)");
        Assert.True(Math.Abs(b1 / kept - 1) <= 0.02, $"B1pMean {b1 * 1e6:F4} µT/√W against {kept * 1e6:F4}");
    }
}

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class CapacitorYieldTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-sar10y-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [PairFixedFact]
    public void TwoHundredTrialsRecombineWithNoEmRun_AndReportPsSarsSpread()
    {
        // port 2 is the pair's other lumped port, about −437 Ω at 1 GHz: 100 nH with the toleranced capacitor nearly resonates it,
        // so the 2 % moves port 2's wave and with it the peak. A copy of the set: the worst trial's re-run writes beside it.
        Directory.CreateDirectory(_dir);
        foreach (string ext in new[] { ".s2p", "_em.npy", ".cemr" })
            File.Copy(Path.Combine(PairCosim.Fixed, PairCosim.Stem + ext), Path.Combine(_dir, PairCosim.Stem + ext));
        string fields = PortFieldSet.FolderFor(Path.Combine(_dir, PairCosim.Stem + ".s2p"));
        Directory.CreateDirectory(fields);
        foreach (string f in Directory.EnumerateFiles(PortFieldSet.FolderFor(Path.Combine(PairCosim.Fixed, PairCosim.Stem + ".s2p"))))
            File.Copy(f, Path.Combine(fields, Path.GetFileName(f)));

        string bench = $"""
            Port:P1 in 0 Num=1 Z=50 Ohm
            EmFieldData:EM1 in p2 File="{Path.Combine(_dir, PairCosim.Stem + ".cemr")}"
            L:L2 p2 mid L=100 nH
            C:C2 mid 0 C=0.84 pF
            analysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1 average=no
            tune C2.C dist=gauss sd=2%
            goal PS = psSAR(F1, 1) analysis=F1 le 1e6 use=yield
            statistics trials=200 seed=1
            """;
        long launches = OpenEmsRun.Invocations, averages = CosimFieldMetrics.FullAverages;
        var run = StatisticalRun.Create(PreparedCircuit.FromText(bench, _dir, _dir), new StatisticalOptions { Mode = StatisticalMode.Yield });
        Assert.Null(run.Refusal);
        var result = run.Run();

        Assert.Equal(200, result.Trials);
        Assert.Equal(0, result.DidNotEvaluate);
        Assert.Equal(launches, OpenEmsRun.Invocations);
        Assert.True(CosimFieldMetrics.FullAverages - averages >= 200, "each trial's psSAR is the full average");
        var ps = result.Records.Select(r => r.Goals.Single().WorstValue).ToArray();
        double mean = ps.Average(), sd = Math.Sqrt(ps.Sum(v => (v - mean) * (v - mean)) / (ps.Length - 1));
        int worst = result.Records[Array.IndexOf(ps, ps.Max())].Trial;
        output.WriteLine($"psSAR1g over 200 trials of C2 ± 2 %: mean {mean:G5} W/kg/W, sd {sd:G3} ({100 * sd / mean:F2} %), " +
                         $"{ps.Min():G5} … {ps.Max():G5} (trial {worst}); no openEMS launch");
        Assert.True(sd > 0, "a 2 % capacitor moved nothing");

        // the worst trial, re-run, writes its recombined field beside the set: the 3D view offers it as fields 'F1 (trial n)'
        var again = run.EvaluateTrial(worst);
        Assert.True(again.Evaluated, again.Reason?.Render());
        Assert.Equal(ps.Max(), again.Goals.Single().WorstValue, ps.Max() * 1e-9);
        Assert.True(File.Exists(Path.Combine(FieldCosimulation.OutputFolder(fields, $"F1 (trial {worst})"), SarVolumeFiles.RecordFile(0))));
    }
}
