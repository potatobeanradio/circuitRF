using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands.Analysis;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for brief-analyses-copy-paste-chains:
/// - CloneAnalysis handles ParametricSweepAnalysis (Bug 1)
/// - Copy expands base selections to whole chains (Bug 2a)
/// - Paste remaps InnerAnalysisName for pasted chains (Bug 2b)
/// </summary>
public sealed class AnalysesCopyPasteChainTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchematicViewModel MakeSchVm(params Analysis[] analyses)
    {
        var model = new SchematicEditModel();
        foreach (var a in analyses) model.Analyses.Add(a);
        return new SchematicViewModel(model, messageSink: null);
    }

    private static AnalysesListViewModel BindListVm(SchematicViewModel schVm)
    {
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(schVm);
        return vm;
    }

    private static ParametricSweepAnalysis MakeSweepValues(
        string name, string varName, double[] values, string inner, bool enabled = true)
        => new ParametricSweepAnalysis(name, varName, values, inner) { Enabled = enabled };

    private static ParametricSweepAnalysis MakeSweepSpec(
        string name, string varName, SweepSpec spec, string inner, bool enabled = false)
        => new ParametricSweepAnalysis(name, varName, spec, inner) { Enabled = enabled };

    // ── Bug 1: CloneAnalysis handles ParametricSweepAnalysis ─────────────────

    [Fact]
    public void CloneAnalysis_SweepValues_NoThrow_PreservesFields()
    {
        var original = MakeSweepValues("DC1_sweep_Vds", "Vds", [1.0, 2.0, 3.0], "DC1", enabled: true);

        var clone = DuplicateAnalysisCommand.CloneAnalysis(original, "DC1_sweep_Vds copy");

        var psa = Assert.IsType<ParametricSweepAnalysis>(clone);
        Assert.Equal("DC1_sweep_Vds copy", psa.Name);
        Assert.Equal("Vds", psa.SweepVarName);
        Assert.Equal([1.0, 2.0, 3.0], psa.SweepValues);
        Assert.True(psa.Enabled);
        Assert.Equal("DC1", psa.InnerAnalysisName);   // original inner preserved
        Assert.Null(psa.Spec);
    }

    [Fact]
    public void CloneAnalysis_SweepSpec_NoThrow_PreservesFields()
    {
        var spec = new SweepSpec(0.0, 1.0, 11, SweepAxisMode.PointCount);
        var original = MakeSweepSpec("DC1_sweep_Vgs", "Vgs", spec, "DC1", enabled: false);

        var clone = DuplicateAnalysisCommand.CloneAnalysis(original, "DC1_sweep_Vgs copy");

        var psa = Assert.IsType<ParametricSweepAnalysis>(clone);
        Assert.Equal("DC1_sweep_Vgs copy", psa.Name);
        Assert.Equal("Vgs", psa.SweepVarName);
        Assert.False(psa.Enabled);
        Assert.Equal("DC1", psa.InnerAnalysisName);
        Assert.NotNull(psa.Spec);
        Assert.Equal(0.0,  psa.Spec!.Start);
        Assert.Equal(1.0,  psa.Spec.Stop);
        Assert.Equal(11,   psa.Spec.StepOrCount);
        Assert.Equal(SweepAxisMode.PointCount, psa.Spec.Mode);
    }

    [Fact]
    public void CloneAnalysis_SweepValues_NewInnerName_HonoredWhenSupplied()
    {
        var original = MakeSweepValues("DC1_sweep_Vds", "Vds", [1.0, 2.0], "DC1");

        var clone = DuplicateAnalysisCommand.CloneAnalysis(original, "DC1 copy_sweep_Vds", "DC1 copy");

        var psa = Assert.IsType<ParametricSweepAnalysis>(clone);
        Assert.Equal("DC1 copy", psa.InnerAnalysisName);
    }

    // ── Bug 2a: ExpandSelectionToChains ──────────────────────────────────────

    [Fact]
    public void ExpandSelectionToChains_SelectBase_IncludesBothSweeps()
    {
        // Model: [DC1, DC1_sweep_Vds, DC1_sweep_Vgs, SP1]
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweepValues("DC1_sweep_Vds", "Vds", [1.0], "DC1");
        var sweepVgs = MakeSweepValues("DC1_sweep_Vgs", "Vgs", [1.0], "DC1_sweep_Vds");
        var sp1      = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", 101));

        var schVm = MakeSchVm(dc1, sweepVds, sweepVgs, sp1);
        var listVm = BindListVm(schVm);

        // Select DC1 only
        listVm.SelectedRow = listVm.Rows[0];   // DC1
        var expanded = listVm.ExpandSelectionToChains([dc1]);

        // Should return [DC1, DC1_sweep_Vds, DC1_sweep_Vgs] in model order
        Assert.Equal(3, expanded.Count);
        Assert.Same(dc1,      expanded[0]);
        Assert.Same(sweepVds, expanded[1]);
        Assert.Same(sweepVgs, expanded[2]);
    }

    [Fact]
    public void ExpandSelectionToChains_SelectBase_NoSweep_ReturnsSelf()
    {
        var sp1 = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", 101));
        var schVm = MakeSchVm(sp1);
        var listVm = BindListVm(schVm);

        var expanded = listVm.ExpandSelectionToChains([sp1]);

        Assert.Single(expanded);
        Assert.Same(sp1, expanded[0]);
    }

    [Fact]
    public void ExpandSelectionToChains_SelectSweepAlone_ReturnsSelf()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweepValues("DC1_sweep_Vds", "Vds", [1.0], "DC1");
        var schVm    = MakeSchVm(dc1, sweepVds);
        var listVm   = BindListVm(schVm);

        // Select only the sweep (not the base)
        var expanded = listVm.ExpandSelectionToChains([sweepVds]);

        Assert.Single(expanded);
        Assert.Same(sweepVds, expanded[0]);
    }

    // ── Bug 2b: Paste remaps InnerAnalysisName ───────────────────────────────

    [Fact]
    public void Paste_Chain_RemapsInnerAnalysisName_NoCollision()
    {
        // Model already has [DC1, DC1_sweep_Vds, DC1_sweep_Vgs].
        // Paste the same chain → names get "copy" suffix; InnerAnalysisName remaps to the new base.
        var existing = new SchematicEditModel();
        existing.Analyses.Add(new DcAnalysis("DC1"));
        existing.Analyses.Add(MakeSweepValues("DC1_sweep_Vds", "Vds", [1.0], "DC1"));
        existing.Analyses.Add(MakeSweepValues("DC1_sweep_Vgs", "Vgs", [1.0], "DC1_sweep_Vds"));

        var toPaste = new List<Analysis>
        {
            new DcAnalysis("DC1"),
            MakeSweepValues("DC1_sweep_Vds", "Vds", [1.0], "DC1"),
            MakeSweepValues("DC1_sweep_Vgs", "Vgs", [1.0], "DC1_sweep_Vds"),
        };

        var cmd = new PasteAnalysesCommand(existing, toPaste);
        cmd.Execute();

        var appended = existing.Analyses.Skip(3).ToList();
        Assert.Equal(3, appended.Count);

        var baseCopy    = Assert.IsType<DcAnalysis>(appended[0]);
        var sweepVdsCopy = Assert.IsType<ParametricSweepAnalysis>(appended[1]);
        var sweepVgsCopy = Assert.IsType<ParametricSweepAnalysis>(appended[2]);

        // Base gets "copy" suffix
        Assert.Equal("DC1 copy", baseCopy.Name);

        // Inner-most sweep points at the copied base
        Assert.Equal("DC1 copy",          sweepVdsCopy.InnerAnalysisName);
        Assert.Equal("DC1_sweep_Vds copy", sweepVgsCopy.InnerAnalysisName);

        // No dangling refs into the original analyses
        Assert.NotEqual("DC1",          sweepVdsCopy.InnerAnalysisName);
        Assert.NotEqual("DC1_sweep_Vds", sweepVgsCopy.InnerAnalysisName);
    }

    [Fact]
    public void Paste_LoneSweep_RetargetsToSelectedAnalysis()
    {
        var existing = new SchematicEditModel();
        existing.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", 101)));

        // Pasting a lone sweep (whose inner "DC1" is not in the paste set)
        var loneSweep = MakeSweepValues("DC1_sweep_Vgs", "Vgs", [1.0], "DC1");
        var cmd = new PasteAnalysesCommand(existing, [loneSweep], retargetInner: "SP1");
        cmd.Execute();

        var pasted = Assert.IsType<ParametricSweepAnalysis>(existing.Analyses[1]);
        Assert.Equal("SP1", pasted.InnerAnalysisName);
    }
}
