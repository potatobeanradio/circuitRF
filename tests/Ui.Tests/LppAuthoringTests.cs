using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Loadpull UI brief 06 gate: authoring a LoadpullPursuitAnalysis in the Add/Edit Analysis dialog VM.
/// The tone is a coefficient + unit pair (var-unit-wins resolves at run time; brief 04b). No Grid field.
/// </summary>
public sealed class LppAuthoringTests
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

    private static AnalysisEditorViewModel NewLppEditor(SchematicEditModel model)
        => new(model, AnalysisEditorViewModel.AnalysisKind.LPP);

    private static void Set(object o, string field, object val) =>
        o.GetType().GetField(field, System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.SetValue(o, val);

    // ── Freq-swept pursuit authoring (FreqSweptLoadpull brief, Layers A–F) ────

    [Fact]
    public void Lpp_SupportsFreqSweep_BuildsChain()
    {
        var model = ModelWithTuners();
        var v = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 800, Y = 0 };
        v.Parameters.Add(new EditableParameter { Name = "RFfreq", Expression = "2", Unit = "GHz" });
        model.Components.Add(v);

        var vm = NewLppEditor(model);
        vm.Name = "LPP1";
        vm.LppBody.LoadTunerName   = "LoadTuner1";
        vm.LppBody.SourceTunerName = "SourceTuner1";
        vm.LppBody.ToneCoeff       = "RFfreq";
        vm.LppBody.ToneUnit        = "GHz";

        Assert.True(vm.ShowSweeps);   // sweep UI available for LPP

        var axis = new SweepAxisRowViewModel(model);
        Set(axis, "_varName",         "RFfreq");
        Set(axis, "_mode",            SweepAxisMode.StepSize);
        Set(axis, "_startExpr",       "1.8");
        Set(axis, "_stopExpr",        "2.2");
        Set(axis, "_stepOrCountExpr", "0.2");
        vm.SweepAxes.Add(axis);

        var chain = vm.BuildAnalyses();
        Assert.NotNull(chain);
        Assert.Equal(2, chain!.Count);
        var lpp = Assert.IsType<LoadpullPursuitAnalysis>(chain[0]);
        var psa = Assert.IsType<ParametricSweepAnalysis>(chain[1]);
        Assert.Equal("LPP1", lpp.Name);
        Assert.Equal("LPP1", psa.InnerAnalysisName);
        Assert.Equal("RFfreq", psa.SweepVarName);
        Assert.Equal(new[] { 1.8e9, 2.0e9, 2.2e9 }, psa.SweepValues);
    }

    // ── BuildAnalyses produces a LoadpullPursuitAnalysis with the set values ──

    [Fact]
    public void Build_LoadpullPursuit_FromForm()
    {
        var vm = NewLppEditor(ModelWithTuners());
        vm.Name    = "LPP1";
        vm.Enabled = true;
        vm.LppBody.LoadTunerName    = "LoadTuner1";
        vm.LppBody.SourceTunerName  = "SourceTuner1";
        vm.LppBody.ToneCoeff        = "2";
        vm.LppBody.ToneUnit         = "GHz";
        vm.LppBody.PinMaxExpr       = "30";
        vm.LppBody.EffTypeExpr      = "PAE";
        vm.LppBody.ZsourceOboExpr   = "6";
        vm.LppBody.SearchMethodExpr = "IteratedQuadratic";
        vm.LppBody.Vswr1Expr        = "1.2";

        var lpp = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(vm.BuildAnalyses()!));

        Assert.Equal("LPP1", lpp.Name);
        Assert.True(lpp.Enabled);
        Assert.Equal("LoadTuner1",   lpp.LoadTunerName);
        Assert.Equal("SourceTuner1", lpp.SourceTunerName);
        Assert.Equal("2",   lpp.ToneExpr);
        Assert.Equal("GHz", lpp.ToneUnit);
        Assert.Equal("30",  lpp.PinMaxExpr);
        Assert.Equal("PAE", lpp.EffTypeExpr);
        Assert.Equal("6",   lpp.ZsourceOBOExpr);
        Assert.Equal("IteratedQuadratic", lpp.SearchMethodExpr);
        Assert.Equal("1.2", lpp.Vswr1Expr);
    }

    // ── Blank OutputGridPath → null on the model ──────────────────────────────

    [Fact]
    public void Build_BlankOutputGrid_IsNull()
    {
        var vm = NewLppEditor(ModelWithTuners());
        vm.LppBody.LoadTunerName   = "LoadTuner1";
        vm.LppBody.SourceTunerName = "SourceTuner1";
        // OutputGridPath left blank.

        var lpp = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(vm.BuildAnalyses()!));
        Assert.Null(lpp.OutputGridPath);
    }

    [Fact]
    public void Build_NonBlankOutputGrid_RoundTrips()
    {
        var vm = NewLppEditor(ModelWithTuners());
        vm.LppBody.LoadTunerName   = "LoadTuner1";
        vm.LppBody.SourceTunerName = "SourceTuner1";
        vm.LppBody.OutputGridPath  = "grids/out.gam";

        var lpp = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(vm.BuildAnalyses()!));
        Assert.Equal("grids/out.gam", lpp.OutputGridPath);
    }

    // ── Tone unit: VAR coefficient + GHz unit survives as a pair ──────────────

    [Fact]
    public void Build_ToneVar_PlusUnit_StoredAsPair()
    {
        var vm = NewLppEditor(ModelWithTuners());
        vm.LppBody.LoadTunerName   = "LoadTuner1";
        vm.LppBody.SourceTunerName = "SourceTuner1";
        vm.LppBody.ToneCoeff       = "RFfreq";
        vm.LppBody.ToneUnit        = "GHz";

        var lpp = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(vm.BuildAnalyses()!));
        Assert.Equal("RFfreq", lpp.ToneExpr);
        Assert.Equal("GHz",    lpp.ToneUnit);
    }

    // ── Follow-on combo gating: disabled when Create off ──────────────────────

    [Fact]
    public void FollowOn_SourceMatchDisabled_WhenCreateOff()
    {
        var vm = NewLppEditor(ModelWithTuners());
        Assert.True(vm.LppBody.FollowOnEnabled);   // default Create = true → enabled

        vm.LppBody.CreateLoadpullResult = false;
        Assert.False(vm.LppBody.FollowOnEnabled);  // gated off

        var lpp = vm.LppBody.BuildAnalysis("LPP1", true);
        Assert.Equal("false", lpp.CreateLoadpullResultExpr);
    }

    // ── Edit round-trip from an existing LoadpullPursuitAnalysis (non-Hz tone) ─

    [Fact]
    public void Edit_RoundTrip_FromExistingPursuit()
    {
        var model = ModelWithTuners();
        var lpp0 = new LoadpullPursuitAnalysis("LPP1")
        {
            Enabled = false,
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            ToneExpr = "RFfreq", ToneUnit = "GHz",
            PinMaxExpr = "28", EffTypeExpr = "PAE", SearchMethodExpr = "IteratedQuadratic",
            CreateLoadpullResultExpr = "false", LoadpullResultZsourceExpr = "MXP",
            KeepNonconvergingExpr = "true", OutputGridPath = "grids/out.gam",
        };
        model.Analyses.Add(lpp0);

        var vm = new AnalysisEditorViewModel(model, lpp0);
        Assert.True(vm.IsLpp);
        Assert.Equal("RFfreq", vm.LppBody.ToneCoeff);
        Assert.Equal("GHz",    vm.LppBody.ToneUnit);
        Assert.True(vm.LppBody.IsEffPae);
        Assert.False(vm.LppBody.CreateLoadpullResult);
        Assert.True(vm.LppBody.KeepNonconverging);
        Assert.Equal("grids/out.gam", vm.LppBody.OutputGridPath);

        var lpp = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(vm.BuildAnalyses()!));
        Assert.False(lpp.Enabled);
        Assert.Equal("RFfreq", lpp.ToneExpr);
        Assert.Equal("GHz",    lpp.ToneUnit);
        Assert.Equal("MXP",    lpp.LoadpullResultZsourceExpr);
        Assert.Equal("false",  lpp.CreateLoadpullResultExpr);
        Assert.Equal("true",   lpp.KeepNonconvergingExpr);
    }

    // ── Required-field gating: blank LoadTuner → null (Grid NOT required) ─────

    [Fact]
    public void Build_BlankLoadTuner_ReturnsNull()
    {
        var vm = NewLppEditor(ModelWithTuners());
        vm.LppBody.SourceTunerName = "SourceTuner1";
        // LoadTunerName left blank → invalid.
        Assert.False(vm.LppBody.IsValid);
        Assert.Null(vm.BuildAnalyses());
    }

    [Fact]
    public void Lpp_IsValid_WithoutGrid()
    {
        var vm = NewLppEditor(ModelWithTuners());
        vm.LppBody.LoadTunerName   = "LoadTuner1";
        vm.LppBody.SourceTunerName = "SourceTuner1";
        // No Grid field for LPP — still valid.
        Assert.True(vm.LppBody.IsValid);
        Assert.True(vm.ShowSweeps);   // sweep UI available for LPP (freq-swept pursuit, Layers A–E)
    }

    // ── Persistence smoke: authored LPP survives .csch round-trip (brief 04) ──

    [Fact]
    public void AuthoredLpp_Survives_CschRoundTrip()
    {
        var vm = NewLppEditor(ModelWithTuners());
        vm.Name = "LPP1";
        vm.LppBody.LoadTunerName    = "LoadTuner1";
        vm.LppBody.SourceTunerName  = "SourceTuner1";
        vm.LppBody.ToneCoeff        = "RFfreq";
        vm.LppBody.ToneUnit         = "GHz";
        vm.LppBody.SearchMethodExpr = "IteratedQuadratic";

        var model = new SchematicEditModel { GridSize = 100 };
        model.Analyses.Add(vm.BuildAnalyses()!.Single());

        string json = SchematicPersistence.Serialize(model);
        var (restored, _, _) = SchematicPersistence.Deserialize(json);

        var lpp = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(restored.Analyses));
        Assert.Equal("LPP1", lpp.Name);
        Assert.Equal("RFfreq", lpp.ToneExpr);
        Assert.Equal("GHz",    lpp.ToneUnit);
        Assert.Equal("IteratedQuadratic", lpp.SearchMethodExpr);
    }
}
