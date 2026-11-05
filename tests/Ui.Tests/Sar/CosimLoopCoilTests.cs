// ================================================================
//  CosimLoopCoilTests.cs — brief-sar-9 R-sar9-6: MRI_Loop_Coil with its three capacitor gaps as ports (LoopPorts.c3d, four
//  50 Ω ports), the 5.4 pF capacitors and the 2.5 Ω feed put back by a schematic (cosim/LoopCosim.cnl), against SAR-8's
//  lumped-capacitor run (a) on the same grid. Both are circuitRF output (testdata/sar/circuitrf/loop).
//
//    CosimLoopCoilTests        routine, fixed data, no process: |B1+| at the phantom centre within 2 %, psSAR10g within 3 %
//                              (overview §4), the planes' correlations reported; and, when this machine still holds the run's
//                              field set, the committed output is what the schematic's fields analysis gives now
//    CosimLoopCoilRerunTests   Benchmark: LoopPorts.c3d through `circuitrf em` on the reference's lines, then `circuitrf fields`
//                              on the schematic, held to the same; CRF_WRITE_SAR9_OUTPUTS=1 writes cosim/output
// ================================================================

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

internal static class LoopCosim
{
    public static readonly string Workspace = LoopReplication.Workspace;
    public static readonly string Folder = Path.Combine(Workspace, "cosim");
    public static readonly string Output = Path.Combine(Folder, "output");
    public const string C3d = "LoopPorts/3d/LoopPorts.c3d", Setup = "CoSim", Stem = "LoopPorts CoSim.openems", Cnl = "LoopCosim.cnl",
                        Analysis = "F1";

    /// <summary>The run's .s4p beside its field set in a workspace's results/.</summary>
    public static string Snp(string workspace) => Path.Combine(workspace, "results", Stem + ".s4p");

    /// <summary>The run's result record. results/ is git-ignored and a run from before records has none, so the first asker adopts it once;
    /// the data files are not touched, so the numbers below it cannot move.</summary>
    public static string Record(string workspace)
    {
        string record = Path.ChangeExtension(Snp(workspace), EmResultNames.RecordExtension);
        if (!File.Exists(record) && File.Exists(Snp(workspace))) EmResultRecord.Adopt(Snp(workspace));
        return record;
    }

    /// <summary>What a recombination left beside the field set of <paramref name="snp"/>, kept in <paramref name="outDir"/>: the
    /// Touchstone, the field set's manifest, the co-simulation record, and SAR-8's record, lines and planes of the recombined field.</summary>
    public static void Collect(string snp, string outDir)
    {
        Directory.CreateDirectory(outDir);
        File.Copy(snp, Path.Combine(outDir, "LoopPorts.s4p"), true);
        string fields = PortFieldSet.FolderFor(snp);
        File.Copy(Path.Combine(fields, PortFieldSet.ManifestFile), Path.Combine(outDir, PortFieldSet.ManifestFile), true);
        string cosim = FieldCosimulation.OutputFolder(fields, Analysis);
        File.Copy(Path.Combine(cosim, FieldCosimulation.RecordFile), Path.Combine(outDir, FieldCosimulation.RecordFile), true);
        LoopReplication.CollectVolumes(cosim, outDir);
        File.WriteAllText(Path.Combine(outDir, "README.md"),
            "# circuitRF OUTPUT for brief-sar-9's co-simulation gate — not a reference\n\n" +
            "`LoopPorts.s4p` and `fields.json` are what `circuitrf em ../../LoopPorts/3d/LoopPorts.c3d --setup CoSim` wrote (the field files " +
            "themselves, 201 MB, stay in results/). `cosim.json`, `sar1.json` and the planes are what `circuitrf fields ../LoopCosim.cnl` " +
            "recombined from them: the schematic's 5.4 pF capacitors and 2.5 Ω feed around the four-port block. CosimLoopCoilTests holds " +
            "them to SAR-8's lumped-capacitor run in `../../reference-grid/`.\n");
    }

    /// <summary>Overview §4 for the co-simulation in <paramref name="dir"/> against SAR-8's run (a): |B1+| per √W at the phantom
    /// centre within 2 %, psSAR10g per W within 3 %; the planes' correlations, peak local SAR and the power split reported.</summary>
    public static void Compare(string dir, ITestOutputHelper output)
    {
        string lumped = Path.Combine(Workspace, "reference-grid");
        var mine = JsonSerializer.Deserialize<SarRunRecord>(File.ReadAllText(Path.Combine(dir, LoopReplication.Record)))!;
        var theirs = JsonSerializer.Deserialize<SarRunRecord>(File.ReadAllText(Path.Combine(lumped, LoopReplication.Record)))!;
        var cosim = JsonSerializer.Deserialize<CosimRecord>(File.ReadAllText(Path.Combine(dir, FieldCosimulation.RecordFile)))!;
        double[][] Lines(string d) => [.. "xyz".Select(a => SarVolumeFiles.ReadFloat64(Path.Combine(d, SarVolumeFiles.LinesFile(0, a))))];
        float[] Plane(string d, string q, string p) => SarVolumeFiles.ReadFloat32(Path.Combine(d, LoopReplication.PlaneFile(q, p))).Values;
        var (lm, lt) = (Lines(dir), Lines(lumped));
        var failures = new List<string>();
        void Hold(bool ok, string what) { output.WriteLine((ok ? "  ok    " : "  MISS  ") + what); if (!ok) failures.Add(what); }
        static double Pct(double a, double b) => 100 * (a / b - 1);

        double b1 = LoopReplication.At(lm[0], lm[1], Plane(dir, SarVolumeFiles.B1PlusQuantity, "z0"), 0, 0) / Math.Sqrt(mine.PerWattDivisor);
        double b1L = LoopReplication.At(lt[0], lt[1], Plane(lumped, SarVolumeFiles.B1PlusQuantity, "z0"), 0, 0) / Math.Sqrt(theirs.AcceptedPowerW);
        Hold(Math.Abs(b1 / b1L - 1) <= 0.02, $"|B1+| at the phantom centre {b1 * 1e6:F4} µT/√W, the lumped run {b1L * 1e6:F4} ({Pct(b1, b1L):+0.00;-0.00} %, 2 %)");
        double ps = mine.PeakPerWatt("SAR10g")!.Value, psL = theirs.PeakPerWatt("SAR10g")!.Value;
        Hold(Math.Abs(ps / psL - 1) <= 0.03, $"psSAR10g {ps:F4} W/kg/W, the lumped run {psL:F4} ({Pct(ps, psL):+0.00;-0.00} %, 3 %)");
        var cube = mine.Peaks.First(p => p.Quantity == "SAR10g");
        var cubeL = theirs.Peaks.First(p => p.Quantity == "SAR10g");
        output.WriteLine($"  the 10 g peak's cell {string.Join(",", cube.Cell)}, the lumped run's {string.Join(",", cubeL.Cell)}");
        output.WriteLine($"  peak local SAR {mine.PeakPerWatt("SAR"):F3} W/kg/W ({Pct(mine.PeakPerWatt("SAR")!.Value, theirs.PeakPerWatt("SAR")!.Value):+0.00;-0.00} %); " +
                         $"mean |B1+| over the brain {mine.B1pMeanPerSqrtWatt * 1e6:F4} µT/√W ({Pct(mine.B1pMeanPerSqrtWatt!.Value, theirs.B1pMeanPerSqrtWatt!.Value):+0.00;-0.00} %); " +
                         $"absorbed {100 * mine.AbsorbedFraction:F1} % of accepted, the lumped run {100 * theirs.AbsorbedFraction:F1} %");
        foreach (var (q, p) in new[] { ("SAR", "z0"), ("SAR", "y0"), ("B1p", "z0"), ("B1p", "y0") })
        {
            var a = Plane(dir, q, p);
            var b = Plane(lumped, q, p);
            double ma = a.Average(v => (double)v), mb = b.Average(v => (double)v), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
            output.WriteLine($"  {q} on the {p} plane correlates {sab / Math.Sqrt(saa * sbb):F5} with the lumped run's");
        }
        output.WriteLine($"  the circuit: |Γ| at the feed {cosim.Reflection.Magnitude:F4}; the block's incident waves |a| " +
                         string.Join(", ", cosim.Incident.Select(w => Math.Sqrt(w[0] * w[0] + w[1] * w[1]).ToString("F3"))) + " √W at 0.5 W available");
        Assert.True(failures.Count == 0, "missed overview §4: " + string.Join("; ", failures));
    }
}

public sealed class CosimLoopFactAttribute : FactAttribute
{
    public CosimLoopFactAttribute()
    {
        if (!File.Exists(Path.Combine(LoopCosim.Output, LoopReplication.Record)))
            Skip = "needs the committed co-simulation output in testdata/sar/circuitrf/loop/cosim/output: CosimLoopCoilRerunTests with CRF_WRITE_SAR9_OUTPUTS=1 makes it";
    }
}

public sealed class CosimLoopFieldSetFactAttribute : FactAttribute
{
    public CosimLoopFieldSetFactAttribute()
    {
        if (!File.Exists(Path.Combine(LoopCosim.Output, LoopReplication.Record)))
            Skip = "needs the committed co-simulation output in testdata/sar/circuitrf/loop/cosim/output";
        else if (PortFieldSet.ReadManifest(PortFieldSet.FolderFor(LoopCosim.Snp(LoopCosim.Workspace)))?.Stamp is not { } stamp ||
                 PortFieldSet.Open(LoopCosim.Record(LoopCosim.Workspace), 298e6) is not { Set: { } set })
            Skip = "needs LoopPorts.c3d's field set, which stays on the machine that ran it (201 MB): CosimLoopCoilRerunTests makes it";
        else
        {
            set.Dispose();
            // the committed output must have been recombined from THIS field set, or the numbers are another run's
            if (PortFieldSet.ReadManifest(LoopCosim.Output)?.Stamp != stamp)
                Skip = "this machine's LoopPorts field set is another run than the committed output's: CosimLoopCoilRerunTests with " +
                       "CRF_WRITE_SAR9_OUTPUTS=1 makes the two one run";
        }
    }
}

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class CosimLoopCoilTests(ITestOutputHelper output)
{
    [CosimLoopFact]
    public void TheCoilWithPortsForCapacitors_ReproducesTheLumpedCapacitorRun() => LoopCosim.Compare(LoopCosim.Output, output);

    [CosimLoopFieldSetFact]
    public void TheCommittedOutputIsWhatTheSchematicsFieldsAnalysisGives()
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromFile(Path.Combine(LoopCosim.Folder, LoopCosim.Cnl), LoopCosim.Folder),
                                             new CircuitEvaluationRequest { Analyses = [LoopCosim.Analysis] });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var now = SarVolumeFiles.ReadRecord(Path.Combine(FieldCosimulation.OutputFolder(PortFieldSet.FolderFor(LoopCosim.Snp(LoopCosim.Workspace)), LoopCosim.Analysis),
                                                         LoopReplication.Record))!;
        var kept = SarVolumeFiles.ReadRecord(Path.Combine(LoopCosim.Output, LoopReplication.Record))!;
        Assert.Equal(kept.PeakPerWatt("SAR10g")!.Value, now.PeakPerWatt("SAR10g")!.Value, kept.PeakPerWatt("SAR10g")!.Value * 1e-6);
        Assert.Equal(kept.B1pMeanPerSqrtWatt!.Value, now.B1pMeanPerSqrtWatt!.Value, kept.B1pMeanPerSqrtWatt!.Value * 1e-6);
        output.WriteLine($"psSAR10g {now.PeakPerWatt("SAR10g"):F5} W/kg/W, mean |B1+| {now.B1pMeanPerSqrtWatt * 1e6:F5} µT/√W, as committed");
    }
}

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class CosimLoopCoilRerunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar9-loop-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [SarReferenceFact]
    [Trait("Category", "Benchmark")]
    public void TheFourPortRunAndTheSchematic_ThroughTheCli_StillReproduceTheLumpedRun()
    {
        Assert.True(SolverDiscovery.ReadinessFor(Em3dSolverId.OpenEms).All(r => r.Proceeds), "needs openEMS");
        // In place: results/ is git-ignored, and the field set the run leaves there is the one the routine test recombines.
        string ws = LoopCosim.Workspace;
        string c3d = Path.Combine(ws, LoopCosim.C3d);

        var (exit, stdout, stderr) = PairSuperpositionRerunTests.Cli("em", c3d, "--setup", LoopCosim.Setup);
        Assert.True(exit == 0, stderr + stdout);
        (exit, stdout, stderr) = PairSuperpositionRerunTests.Cli("fields", Path.Combine(ws, "cosim", LoopCosim.Cnl));
        Assert.True(exit == 0, stderr + stdout);
        output.WriteLine(stdout.Split('\n').FirstOrDefault(l => l.StartsWith("SAR at", StringComparison.Ordinal)) ?? "");

        string outDir = Path.Combine(_root, "output");
        LoopCosim.Collect(LoopCosim.Snp(ws), outDir);
        LoopCosim.Compare(outDir, output);
        if (Environment.GetEnvironmentVariable("CRF_WRITE_SAR9_OUTPUTS") == "1") LoopCosim.Collect(LoopCosim.Snp(ws), LoopCosim.Output);
    }
}
