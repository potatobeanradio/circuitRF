using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Loadpull UI brief 05 gate: authoring a LoadpullAnalysis in the Add/Edit Analysis dialog VM.
/// The tone is a coefficient + unit pair (var-unit-wins resolves at run time; brief 04b).
/// </summary>
public sealed class LpAuthoringTests
{
    private static SchematicEditModel ModelWithTuners()
    {
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent
            { InstanceName = "LoadTuner1",   Symbol = SymbolKind.LoadTuner,   X = 0, Y = 0 });
        m.Components.Add(new EditableComponent
            { InstanceName = "SourceTuner1", Symbol = SymbolKind.SourceTuner, X = 400, Y = 0 });
        return m;
    }

    private static AnalysisEditorViewModel NewLpEditor(SchematicEditModel model)
        => new(model, AnalysisEditorViewModel.AnalysisKind.LP);

    // ── Tuner pickers populate from tuner components ──────────────────────────

    [Fact]
    public void LpBody_TunerInstanceNames_ListsTuners()
    {
        var vm = NewLpEditor(ModelWithTuners());
        Assert.Equal(new[] { "LoadTuner1", "SourceTuner1" }, vm.LpBody.TunerInstanceNames.ToArray());
        Assert.False(vm.LpBody.HasNoTuners);
    }

    [Fact]
    public void LpBody_EmptySchematic_HasNoTuners()
    {
        var vm = NewLpEditor(new SchematicEditModel());
        Assert.True(vm.LpBody.HasNoTuners);
    }

    // ── BuildAnalyses produces a LoadpullAnalysis with the set values ─────────

    [Fact]
    public void Build_LoadpullAnalysis_FromForm()
    {
        var vm = NewLpEditor(ModelWithTuners());
        vm.Name    = "LP1";
        vm.Enabled = true;
        vm.LpBody.LoadTunerName   = "LoadTuner1";
        vm.LpBody.SourceTunerName = "SourceTuner1";
        vm.LpBody.GridPath        = "grids/hero3.gam";
        vm.LpBody.ToneCoeff       = "2";
        vm.LpBody.ToneUnit        = "GHz";
        vm.LpBody.PinMaxExpr      = "20";
        vm.LpBody.CompressionExpr = "3";

        var result = vm.BuildAnalyses();
        var lp = Assert.IsType<LoadpullAnalysis>(Assert.Single(result!));

        Assert.Equal("LP1", lp.Name);
        Assert.True(lp.Enabled);
        Assert.Equal("LoadTuner1",   lp.LoadTunerName);
        Assert.Equal("SourceTuner1", lp.SourceTunerName);
        Assert.Equal("grids/hero3.gam", lp.GridPath);
        Assert.Equal("2",   lp.ToneExpr);
        Assert.Equal("GHz", lp.ToneUnit);
        Assert.Equal("20",  lp.PinMaxExpr);
        Assert.Equal("3",   lp.CompressionExpr);
    }

    // ── Tone unit: VAR coefficient + GHz unit survives as a pair ──────────────

    [Fact]
    public void Build_ToneVar_PlusUnit_StoredAsPair()
    {
        var vm = NewLpEditor(ModelWithTuners());
        vm.LpBody.LoadTunerName   = "LoadTuner1";
        vm.LpBody.SourceTunerName = "SourceTuner1";
        vm.LpBody.GridPath        = "g.gam";
        vm.LpBody.ToneCoeff       = "RFfreq";
        vm.LpBody.ToneUnit        = "GHz";

        var lp = Assert.IsType<LoadpullAnalysis>(Assert.Single(vm.BuildAnalyses()!));
        Assert.Equal("RFfreq", lp.ToneExpr);
        Assert.Equal("GHz",    lp.ToneUnit);
    }

    // ── Edit round-trip from an existing LoadpullAnalysis (non-Hz tone) ───────

    [Fact]
    public void Edit_RoundTrip_FromExistingLoadpull()
    {
        var model = ModelWithTuners();
        var lp0 = new LoadpullAnalysis("LP1")
        {
            Enabled = false,
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            GridPath = "grids/hero3.gam",
            ToneExpr = "RFfreq", ToneUnit = "GHz",
            PinMaxExpr = "25", CompressionExpr = "1", GainTypeExpr = "Gp", SweepExpr = "Source",
        };
        model.Analyses.Add(lp0);

        var vm = new AnalysisEditorViewModel(model, lp0);
        Assert.True(vm.IsLp);
        Assert.Equal("RFfreq", vm.LpBody.ToneCoeff);
        Assert.Equal("GHz",    vm.LpBody.ToneUnit);
        Assert.Equal("Gp",     vm.LpBody.GainTypeExpr);
        Assert.True(vm.LpBody.IsGainGp);
        Assert.True(vm.LpBody.IsSweepSource);

        var lp = Assert.IsType<LoadpullAnalysis>(Assert.Single(vm.BuildAnalyses()!));
        Assert.False(lp.Enabled);
        Assert.Equal("RFfreq", lp.ToneExpr);
        Assert.Equal("GHz",    lp.ToneUnit);
        Assert.Equal("25",     lp.PinMaxExpr);
    }

    // ── Required-field gating ──────────────────────────────────────────────────

    [Fact]
    public void Build_BlankLoadTuner_ReturnsNull()
    {
        var vm = NewLpEditor(ModelWithTuners());
        vm.LpBody.SourceTunerName = "SourceTuner1";
        vm.LpBody.GridPath        = "g.gam";
        // LoadTunerName left blank → invalid.
        Assert.False(vm.LpBody.IsValid);
        Assert.Null(vm.BuildAnalyses());
    }

    [Fact]
    public void Build_BlankGrid_ReturnsNull()
    {
        var vm = NewLpEditor(ModelWithTuners());
        vm.LpBody.LoadTunerName   = "LoadTuner1";
        vm.LpBody.SourceTunerName = "SourceTuner1";
        // GridPath left blank → invalid.
        Assert.False(vm.LpBody.IsValid);
        Assert.Null(vm.BuildAnalyses());
    }

    // ── LP ignores sweep axes (v1) ────────────────────────────────────────────

    [Fact]
    public void Lp_IgnoresSweepAxes_ReturnsSingleAnalysis()
    {
        var model = ModelWithTuners();
        var vm = NewLpEditor(model);
        vm.LpBody.LoadTunerName   = "LoadTuner1";
        vm.LpBody.SourceTunerName = "SourceTuner1";
        vm.LpBody.GridPath        = "g.gam";
        Assert.False(vm.ShowSweeps);   // sweep UI hidden for LP

        // Even if a sweep axis sneaks in, LP returns just the base (no chain in v1).
        vm.SweepAxes.Add(new SweepAxisRowViewModel(model));
        var result = vm.BuildAnalyses();
        Assert.Single(result!);
        Assert.IsType<LoadpullAnalysis>(result![0]);
    }

    // ── Persistence smoke: authored LP survives .csch round-trip (brief 04) ───

    [Fact]
    public void AuthoredLp_Survives_CschRoundTrip()
    {
        var vm = NewLpEditor(ModelWithTuners());
        vm.Name = "LP1";
        vm.LpBody.LoadTunerName   = "LoadTuner1";
        vm.LpBody.SourceTunerName = "SourceTuner1";
        vm.LpBody.GridPath        = "grids/hero3.gam";
        vm.LpBody.ToneCoeff       = "RFfreq";
        vm.LpBody.ToneUnit        = "GHz";

        var model = new SchematicEditModel { GridSize = 100 };
        model.Analyses.Add(vm.BuildAnalyses()!.Single());

        string json = SchematicPersistence.Serialize(model);
        var (restored, _, _) = SchematicPersistence.Deserialize(json);

        var lp = Assert.IsType<LoadpullAnalysis>(Assert.Single(restored.Analyses));
        Assert.Equal("LP1", lp.Name);
        Assert.Equal("RFfreq", lp.ToneExpr);
        Assert.Equal("GHz",    lp.ToneUnit);
        Assert.Equal("LoadTuner1", lp.LoadTunerName);
    }
}
