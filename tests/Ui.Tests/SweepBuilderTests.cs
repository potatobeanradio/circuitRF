using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the parametric-sweep chain builder in <see cref="AnalysisEditorViewModel"/>.
/// Build_NestedChain: two sweep axes around HB → correct chain.
/// Migrate_OldHbSweep: legacy HB SweepVar* fields → one pre-populated sweep row.
/// </summary>
public sealed class SweepBuilderTests
{
    // ── Test 1: two sweep axes around HB → correct chain ─────────────────────

    [Fact]
    public void Build_NestedChain_TwoAxes_HB()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.HB);
        vm.Name    = "HB1";
        vm.Enabled = true;

        // Axis 0 (inner): Pavl, List, 5 pts
        var axis0 = new SweepAxisRowViewModel(model);
        SetField(axis0, "_varName",  "Pavl");
        SetField(axis0, "_mode",     SweepAxisMode.List);
        SetField(axis0, "_listExpr", "-20, -15, -10, -5, 0");
        vm.SweepAxes.Add(axis0);

        // Axis 1 (outer): Vbias, StepSize, 0→1 step 0.5 → 3 pts
        var axis1 = new SweepAxisRowViewModel(model);
        SetField(axis1, "_varName",         "Vbias");
        SetField(axis1, "_mode",            SweepAxisMode.StepSize);
        SetField(axis1, "_startExpr",       "0");
        SetField(axis1, "_stopExpr",        "1");
        SetField(axis1, "_stepOrCountExpr", "0.5");
        vm.SweepAxes.Add(axis1);

        var chain = vm.BuildAnalyses();

        // Must return 3 analyses: HB1 (disabled) + HB1_sweep_Pavl (disabled) + HB1_sweep_Vbias (enabled)
        Assert.NotNull(chain);
        Assert.Equal(3, chain!.Count);

        var hb = chain[0] as HarmonicBalanceAnalysis;
        Assert.NotNull(hb);
        Assert.Equal("HB1", hb!.Name);
        Assert.False(hb.Enabled);

        var sweepPavl = chain[1] as ParametricSweepAnalysis;
        Assert.NotNull(sweepPavl);
        Assert.Equal("HB1_sweep_Pavl", sweepPavl!.Name);
        Assert.Equal("Pavl", sweepPavl.SweepVarName);
        Assert.Equal("HB1",  sweepPavl.InnerAnalysisName);
        Assert.Equal(5,      sweepPavl.SweepValues.Length);
        Assert.False(sweepPavl.Enabled);

        var sweepVbias = chain[2] as ParametricSweepAnalysis;
        Assert.NotNull(sweepVbias);
        Assert.Equal("HB1_sweep_Vbias", sweepVbias!.Name);
        Assert.Equal("Vbias",           sweepVbias.SweepVarName);
        Assert.Equal("HB1_sweep_Pavl",  sweepVbias.InnerAnalysisName);
        Assert.Equal(3,                 sweepVbias.SweepValues.Length); // 0, 0.5, 1.0
        Assert.True(sweepVbias.Enabled);
    }

    // ── Test 2: no sweep axes → single enabled analysis ───────────────────────

    [Fact]
    public void Build_NoAxes_SingleEnabledAnalysis()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name    = "DC1";
        vm.Enabled = true;

        var chain = vm.BuildAnalyses();

        Assert.NotNull(chain);
        Assert.Single(chain!);
        Assert.IsType<DcAnalysis>(chain[0]);
        Assert.True(chain[0].Enabled);
    }

    // ── Test 3: Migrate_OldHbSweep → one pre-populated sweep row ─────────────

    [Fact]
    public void Migrate_OldHbSweep_PrePopulatesOneRow()
    {
        var model = new SchematicEditModel();

        // Construct legacy HB with old deprecated sweep fields.
#pragma warning disable CS0618
        var legacyHb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr      = "1e9",
            MaxHarmonicExpr = "7",
            SweepVarName  = "Pavl",
            SweepStartExpr = "-20",
            SweepStopExpr  = "0",
            SweepStepExpr  = "1",
        };
#pragma warning restore CS0618

        model.Analyses.Add(legacyHb);

        // Open edit dialog VM: legacy fields should migrate into a sweep row.
        var vm = new AnalysisEditorViewModel(model, legacyHb);

        Assert.Single(vm.SweepAxes);
        var row = vm.SweepAxes[0];
        Assert.Equal("Pavl",      row.VarName);
        Assert.Equal(SweepAxisMode.StepSize, row.Mode);
        Assert.Equal("-20", row.StartExpr);
        Assert.Equal("0",   row.StopExpr);
        Assert.Equal("1",   row.StepOrCountExpr);
    }

    // ── Test 4: edit a ParametricSweepAnalysis → loads inner + chain ──────────

    [Fact]
    public void Edit_OuterSweepAnalysis_LoadsChain()
    {
        var model = new SchematicEditModel();

        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "1e9", MaxHarmonicExpr = "7",
        };
        hb.Enabled = false;

        var psa = new ParametricSweepAnalysis("HB1_sweep_Pavl", "Pavl",
            new double[] { -20, -15, -10 }, "HB1");
        psa.Enabled = true;

        model.Analyses.Add(hb);
        model.Analyses.Add(psa);

        // Open editor on the OUTER sweep — should load HB fields + 1 sweep row.
        var vm = new AnalysisEditorViewModel(model, psa);

        Assert.Equal(AnalysisEditorViewModel.AnalysisKind.HB, vm.Type);
        Assert.Equal("HB1", vm.Name);
        Assert.Single(vm.SweepAxes);
        Assert.Equal("Pavl", vm.SweepAxes[0].VarName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Set private backing field via reflection (CommunityToolkit.MVVM generates them).
    private static void SetField<T>(object obj, string fieldName, T value)
    {
        var fi = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        fi?.SetValue(obj, value);
    }
}
