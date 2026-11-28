// Owner: PCD-9 (showcase examples). One test per showcase cell: designed through the window's functions, the design check
// clean, a bench placing it laid out and LVS clean; the Wilkinson's circuit numbers.

using CircuitRF.Design.PCellDesigner;
using CircuitRF.Diagnostics;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd9ShowcaseTests : IDisposable
{
    private readonly string _ws = Pcd9Cells.Workspace(Path.Combine(Path.GetTempPath(), "crf-pcd9-" + Guid.NewGuid().ToString("N")[..12]));

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort */ }
    }

    private static string Say(IEnumerable<Diagnostic> findings) => string.Join("\n", findings.Select(f => $"{f.Severity} {f.Id}: {f.Render()}"));

    /// <summary>Designed, check clean, LVS clean on its own saved layout, its bench laid out with no warning and LVS clean;
    /// the bench's path.</summary>
    private string DesignedAndPlaced(Pcd9Spec spec, Func<string, string, string> bench)
    {
        string cell = Pcd9Cells.Design(_ws, spec);
        var check = PCellDesignCheck.Run(cell);
        Assert.True(check.All(f => f.Severity != DiagnosticSeverity.Error), Say(check));
        using (GeneratedCellsRun.Prepare(Path.Combine(cell, "layout", spec.Cell + ".clay")))
        {
            var own = CircuitRF.Design.Layout.Lvs.LvsRun.Run(cell);
            Assert.True(own.ErrorCount == 0, string.Join("\n", own.Findings.Select(f => f.Diagnostic)));
        }
        string path = bench(_ws, cell);
        var laid = LayoutFromSchematic.Run(path);
        Assert.True(laid.Refusal is null, laid.Refusal);
        Assert.DoesNotContain(laid.Lines, l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning);
        var lvs = CircuitRF.Design.Layout.Lvs.LvsRun.Run(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
        Assert.True(lvs.ErrorCount == 0, string.Join("\n", lvs.Findings.Select(f => f.Diagnostic)));
        return path;
    }

    /// <summary>The 3-way Wilkinson at 2.45 GHz: S21 = S31 = S41 ≈ −4.77 dB (within the board's loss), the input matched,
    /// every pair of outputs isolated.</summary>
    [Fact]
    public void Wilkinson3_SplitsInThreeAt2_45GHz_Matched_Isolated()
    {
        string bench = DesignedAndPlaced(Pcd9Cells.Wilkinson, Pcd9Cells.WilkinsonBench);
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(bench), new CircuitEvaluationRequest { Em = false, Analyses = ["SP1"] });
        Assert.True(r.Status == RunStatus.Success, r.StatusMessage);
        var snp = DataSetBuilder.ToSnp(r.Results.Single(a => a.Name == "SP1").Data);
        int k = Array.FindIndex(snp.Frequencies, f => Math.Abs(f - 2.45e9) < 1e6);
        double Db(int i, int j) => 20 * Math.Log10(snp.Matrices[k][i - 1, j - 1].Magnitude);
        foreach (int i in new[] { 2, 3, 4 }) Assert.InRange(Db(i, 1), -5.2, -4.77);
        Assert.True(Db(1, 1) < -20, $"S11 = {Db(1, 1):F1} dB");
        foreach (var (i, j) in new[] { (3, 2), (4, 2), (4, 3) }) Assert.True(Db(i, j) < -20, $"S{i}{j} = {Db(i, j):F1} dB");
    }

    /// <summary>The spurline: EM only, its slit's far end a drag handle on Ls.</summary>
    [Fact]
    public void Spurline_EmOnly_SlitFollowsLs()
    {
        string bench = DesignedAndPlaced(Pcd9Cells.Spurline, Pcd9Cells.SpurlineBench);
        var refused = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(bench), new CircuitEvaluationRequest { Em = false, Analyses = ["SP1"] });
        Assert.Contains("no circuit model", refused.StatusMessage);
        using (DesignedPCellResolver.Register(_ws))
        {
            Assert.True(PCellRegistry.TryGet("cpcell:Spurline", out var generator));
            var tech = TechPersistence.LoadFromFile(Path.Combine(_ws, "tech", Pcd8PadTee.Tech));
            var handle = Assert.Single(generator(new Dictionary<string, PCellValue>(), tech, PCellLayerSelection.Default).Handles ?? []);
            Assert.Equal("Ls", handle.Parameter);
        }
    }

    /// <summary>The dual-mode patch: the patch and the second feed placed by offset, the gap s from the feeds.</summary>
    [Fact]
    public void DualModePatch_PlacedByOffsets_EmOnly() => DesignedAndPlaced(Pcd9Cells.Patch, Pcd9Cells.PatchBench);

    /// <summary>The slotline divider: its slotted ground drawn on Bottom Copper and carried by the generator, with no pour.</summary>
    [Fact]
    public void Slotline_GroundIsDrawnWithItsSlot()
    {
        DesignedAndPlaced(Pcd9Cells.Slotline, Pcd9Cells.SlotlineBench);
        using (DesignedPCellResolver.Register(_ws))
        {
            Assert.True(PCellRegistry.TryGet("cpcell:Slotline", out var generator));
            var tech = TechPersistence.LoadFromFile(Path.Combine(_ws, "tech", Pcd8PadTee.Tech));
            var shapes = generator(new Dictionary<string, PCellValue>(), tech, PCellLayerSelection.Default).Shapes;
            Assert.Single(shapes, s => s.Layer == new LayerKey(2, 0));   // SH1, the slotted ground, and no pour
        }
    }
}
