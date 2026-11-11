using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate for what the Add Analysis dialog OPENS with (<c>AnalysisSeeding</c>): a field the schematic
/// answers unambiguously is prefilled, and a field it does not answer keeps the body's own default.
///
/// The two halves that matter are tested separately because they fail differently: prefilling the
/// wrong value is visible in the dialog, while prefilling a field the schematic could not actually
/// answer — two load tuners, a unitless frequency — is a wrong run that looks entirely normal.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class AnalysisSeedingTests : FirstLaunchUserState
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static SchematicEditModel ModelWithTuners()
    {
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent
            { InstanceName = "LoadTuner1",   Symbol = SymbolKind.LoadTuner,   X = 0,   Y = 0 });
        m.Components.Add(new EditableComponent
            { InstanceName = "SourceTuner1", Symbol = SymbolKind.SourceTuner, X = 400, Y = 0 });
        return m;
    }

    private static void AddVar(SchematicEditModel m, string name, string expr, string unit,
                               string instance = "VAR1")
    {
        var v = new EditableComponent { InstanceName = instance, Symbol = SymbolKind.Var, X = 800, Y = 0 };
        v.Parameters.Add(new EditableParameter { Name = name, Expression = expr, Unit = unit });
        m.Components.Add(v);
    }

    private static void AddP1Tone(SchematicEditModel m, string freq, string unit,
                                  string instance = "PORT1")
    {
        var p = new EditableComponent { InstanceName = instance, Symbol = SymbolKind.P1Tone, X = -400, Y = 0 };
        p.Parameters.Add(new EditableParameter { Name = "Num",  Expression = "1", Unit = "" });
        p.Parameters.Add(new EditableParameter { Name = "Freq", Expression = freq, Unit = unit });
        m.Components.Add(p);
    }

    private static LoadpullPursuitAnalysis Pursuit(string name = "LPP1") => new(name)
    {
        LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
        ToneExpr = "RFfreq", ToneUnit = "GHz",
        PinStartExpr = "-5", PinMaxExpr = "28", PinStepExpr = "2", CompressionExpr = "4",
    };

    private static AnalysisEditorViewModel NewLp(SchematicEditModel m)
        => new(m, AnalysisEditorViewModel.AnalysisKind.LP);

    private static AnalysisEditorViewModel NewLpp(SchematicEditModel m)
        => new(m, AnalysisEditorViewModel.AnalysisKind.LPP);

    private static AnalysisEditorViewModel NewHb(SchematicEditModel m)
        => new(m, AnalysisEditorViewModel.AnalysisKind.HB);

    // ── Tuners: one of a side answers it; two do not ──────────────────────────

    [Fact]
    public void SingleSourceAndLoadTuner_PrefillBothPickers()
    {
        var vm = NewLp(ModelWithTuners());
        Assert.Equal("LoadTuner1",   vm.LpBody.LoadTunerName);
        Assert.Equal("SourceTuner1", vm.LpBody.SourceTunerName);
    }

    [Fact]
    public void TwoLoadTuners_LeaveTheLoadPickerBlank_SourceStillPrefilled()
    {
        var m = ModelWithTuners();
        m.Components.Add(new EditableComponent
            { InstanceName = "LoadTuner2", Symbol = SymbolKind.LoadTuner, X = 0, Y = 400 });

        var vm = NewLp(m);
        Assert.Equal("", vm.LpBody.LoadTunerName);          // ambiguous — never guessed
        Assert.Equal("SourceTuner1", vm.LpBody.SourceTunerName);
    }

    [Fact]
    public void GenericTunerSymbols_AreNeverAssignedToASide()
    {
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent
            { InstanceName = "T1", Symbol = SymbolKind.Tuner, X = 0, Y = 0 });

        var vm = NewLp(m);
        Assert.Equal("", vm.LpBody.LoadTunerName);
        Assert.Equal("", vm.LpBody.SourceTunerName);
    }

    [Fact]
    public void AnAnalysisNamingATuner_DisambiguatesTwoOfASide()
    {
        var m = ModelWithTuners();
        m.Components.Add(new EditableComponent
            { InstanceName = "LoadTuner2", Symbol = SymbolKind.LoadTuner, X = 0, Y = 400 });
        m.Analyses.Add(new LoadpullPursuitAnalysis("LPP1")
            { LoadTunerName = "LoadTuner2", SourceTunerName = "SourceTuner1" });

        var vm = NewLp(m);
        Assert.Equal("LoadTuner2", vm.LpBody.LoadTunerName);
    }

    [Fact]
    public void ATunerTheAnalysisNamesButTheSchematicLacks_FallsBackToTheOnePlaced()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(new LoadpullPursuitAnalysis("LPP1")
            { LoadTunerName = "Deleted", SourceTunerName = "SourceTuner1" });

        var vm = NewLp(m);
        Assert.Equal("LoadTuner1", vm.LpBody.LoadTunerName);
    }

    // ── The case this exists for: a Loadpull added beside a Pursuit ───────────

    [Fact]
    public void Lp_BorrowsTuners_Tone_AndDriveLadder_FromAnExistingPursuit()
    {
        var m = ModelWithTuners();
        AddVar(m, "RFfreq", "2", "GHz");
        m.Analyses.Add(Pursuit());

        var vm = NewLp(m);
        Assert.Equal("LoadTuner1",   vm.LpBody.LoadTunerName);
        Assert.Equal("SourceTuner1", vm.LpBody.SourceTunerName);
        Assert.Equal("RFfreq",       vm.LpBody.ToneCoeff);
        Assert.Equal("GHz",          vm.LpBody.ToneUnit);
        Assert.Equal("-5",           vm.LpBody.PinStartExpr);
        Assert.Equal("28",           vm.LpBody.PinMaxExpr);
        Assert.Equal("2",            vm.LpBody.PinStepExpr);
        Assert.Equal("4",            vm.LpBody.CompressionExpr);
    }

    [Fact]
    public void Lpp_BorrowsFromAnExistingLoadpull_TheOtherDirection()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(new LoadpullAnalysis("LP1")
        {
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            GridPath = "grids/hero3.gam", ToneExpr = "3.5", ToneUnit = "GHz", PinMaxExpr = "33",
        });

        var vm = NewLpp(m);
        Assert.Equal("3.5", vm.LppBody.ToneCoeff);
        Assert.Equal("GHz", vm.LppBody.ToneUnit);
        Assert.Equal("33",  vm.LppBody.PinMaxExpr);
    }

    [Fact]
    public void SameKindWins_OverTheOtherLoadpullKind()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(Pursuit());                                  // PinMax 28, listed FIRST
        m.Analyses.Add(new LoadpullAnalysis("LP1")
        {
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            GridPath = "g.gam", ToneExpr = "1.9", ToneUnit = "GHz", PinMaxExpr = "31",
        });

        Assert.Equal("31", NewLp(m).LpBody.PinMaxExpr);    // the LP, though it is second in the list
        Assert.Equal("28", NewLpp(m).LppBody.PinMaxExpr);  // the LPP
    }

    // ── The grid: an existing one, else the file a pursuit is set to write ────

    [Fact]
    public void Grid_ComesFromAnExistingLoadpull()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(new LoadpullAnalysis("LP1")
        {
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            GridPath = "grids/hero3.gam", ToneExpr = "2", ToneUnit = "GHz",
        });

        Assert.Equal("grids/hero3.gam", NewLp(m).LpBody.GridPath);
    }

    [Fact]
    public void Grid_FallsBackToTheGridAPursuitWrites()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(new LoadpullPursuitAnalysis("LPP1")
            { LoadTunerName = "LoadTuner1", OutputGridPath = "grids/pursuit.gam" });

        Assert.Equal("grids/pursuit.gam", NewLp(m).LpBody.GridPath);
    }

    [Fact]
    public void Grid_StaysBlank_WhenNothingNamesOne()
    {
        Assert.Equal("", NewLp(ModelWithTuners()).LpBody.GridPath);
    }

    // ── The tone, when no analysis declares one ──────────────────────────────

    [Fact]
    public void Tone_ComesFromTheOneToneSourceOnTheSchematic()
    {
        var m = ModelWithTuners();
        AddP1Tone(m, "RFfreq", "GHz");

        var vm = NewLp(m);
        Assert.Equal("RFfreq", vm.LpBody.ToneCoeff);
        Assert.Equal("GHz",    vm.LpBody.ToneUnit);
    }

    [Fact]
    public void Tone_IgnoresAToneSourceWithNoFrequencyUnit()
    {
        // A unitless "2" read into a GHz field is off by 1e9 and nothing downstream would flag it,
        // so an unstated unit seeds nothing at all — the body keeps its own default.
        var m = ModelWithTuners();
        AddP1Tone(m, "2", "");

        var vm = NewLp(m);
        Assert.Equal("1",   vm.LpBody.ToneCoeff);
        Assert.Equal("GHz", vm.LpBody.ToneUnit);
    }

    [Fact]
    public void Tone_FallsBackToTheOneFrequencyVar_ByName()
    {
        var m = ModelWithTuners();
        AddVar(m, "RFfreq", "2", "GHz");

        var vm = NewLp(m);
        Assert.Equal("RFfreq", vm.LpBody.ToneCoeff);   // the NAME, not the number it holds today
        Assert.Equal("GHz",    vm.LpBody.ToneUnit);
    }

    [Fact]
    public void Tone_TwoFrequencyVars_PrefersTheOneNamedLikeAFrequency()
    {
        var m = ModelWithTuners();
        AddVar(m, "IFfreq", "0.1", "GHz", "VAR1");
        AddVar(m, "Fspace", "0.01", "GHz", "VAR2");

        // "IFfreq" is the only one reading as a frequency → unique → used.
        Assert.Equal("IFfreq", NewLp(m).LpBody.ToneCoeff);
    }

    [Fact]
    public void Tone_TwoFrequencyVarsBothNamedLikeAFrequency_SeedNothing()
    {
        var m = ModelWithTuners();
        AddVar(m, "RFfreq", "2",   "GHz", "VAR1");
        AddVar(m, "LOfreq", "2.1", "GHz", "VAR2");

        var vm = NewLp(m);
        Assert.Equal("1",   vm.LpBody.ToneCoeff);
        Assert.Equal("GHz", vm.LpBody.ToneUnit);
    }

    [Fact]
    public void Tone_AnAnalysisBeatsAToneSource()
    {
        var m = ModelWithTuners();
        AddP1Tone(m, "1.8", "GHz");
        m.Analyses.Add(Pursuit());      // ToneExpr RFfreq

        Assert.Equal("RFfreq", NewLp(m).LpBody.ToneCoeff);
    }

    [Fact]
    public void Tone_StoredInHz_IsSplitForDisplay_LikeAnEdit()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(new LoadpullAnalysis("LP1")
        {
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            GridPath = "g.gam", ToneExpr = "2.4e9", ToneUnit = "Hz",
        });

        var vm = NewLpp(m);
        Assert.Equal("2.4", vm.LppBody.ToneCoeff);
        Assert.Equal("GHz", vm.LppBody.ToneUnit);
    }

    [Fact]
    public void Tone_UnsetOnEveryAnalysis_SeedsNothing()
    {
        // "0" is the unset default every analysis type carries, not a frequency.
        var m = ModelWithTuners();
        m.Analyses.Add(new LoadpullAnalysis("LP1")
            { LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1", GridPath = "g.gam" });

        var vm = NewLpp(m);
        Assert.Equal("1",   vm.LppBody.ToneCoeff);
        Assert.Equal("GHz", vm.LppBody.ToneUnit);
    }

    // ── HB borrows the tone and nothing else ─────────────────────────────────

    [Fact]
    public void Hb_BorrowsTheToneFromALoadpull()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(Pursuit());

        var vm = NewHb(m);
        Assert.Equal("RFfreq", vm.HbBody.ToneCoeff);
        Assert.Equal("GHz",    vm.HbBody.ToneUnit);
        Assert.Equal("7",      vm.HbBody.MaxHarmonicExpr);   // its own default, not the pursuit's 5
        var hb = vm.HbBody.BuildAnalysis("HB1", enabled: true);
        Assert.Equal("RFfreq", hb.ToneExpr);
        Assert.Equal("GHz",    hb.ToneUnit);
    }

    [Fact]
    public void Hb_PrefersAnotherHbsTone()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(Pursuit());
        m.Analyses.Add(new HarmonicBalanceAnalysis("HB1") { ToneExpr = "5", ToneUnit = "GHz" });

        Assert.Equal("5", NewHb(m).HbBody.ToneCoeff);
    }

    // ── A seeded tone is the same value the dialog would build from an edit ──

    [Fact]
    public void ASeededToneSurvivesBuild_WithoutRescaling()
    {
        var m = ModelWithTuners();
        m.Analyses.Add(new LoadpullPursuitAnalysis("LPP1")
        {
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            ToneExpr = "1.85", ToneUnit = "MHz",
        });

        var vm = NewLp(m);
        vm.LpBody.GridPath = "g.gam";
        var lp = Assert.IsType<LoadpullAnalysis>(Assert.Single(vm.BuildAnalyses()!));

        // "1.85" MHz, not the 1850 a unit assignment made in the wrong order would have produced.
        Assert.Equal("1.85", lp.ToneExpr);
        Assert.Equal("MHz",  lp.ToneUnit);
    }

    // ── Editing: the bodies the edited analysis did not fill are still seeded ─

    [Fact]
    public void Editing_APursuit_LeavesTheLpBodyReadyForATypeSwitch()
    {
        var m = ModelWithTuners();
        var lpp = Pursuit();
        m.Analyses.Add(lpp);

        var vm = new AnalysisEditorViewModel(m, lpp);
        Assert.True(vm.IsLpp);
        Assert.Equal("LoadTuner1",   vm.LpBody.LoadTunerName);
        Assert.Equal("SourceTuner1", vm.LpBody.SourceTunerName);
        Assert.Equal("RFfreq",       vm.LpBody.ToneCoeff);
        Assert.Equal("28",           vm.LpBody.PinMaxExpr);
    }

    // ── An empty schematic changes nothing ───────────────────────────────────

    [Fact]
    public void EmptySchematic_LeavesEveryDefaultAlone()
    {
        var vm = NewLp(new SchematicEditModel());
        Assert.Equal("",    vm.LpBody.LoadTunerName);
        Assert.Equal("",    vm.LpBody.SourceTunerName);
        Assert.Equal("",    vm.LpBody.GridPath);
        Assert.Equal("1",   vm.LpBody.ToneCoeff);
        Assert.Equal("GHz", vm.LpBody.ToneUnit);
        Assert.Equal("-20", vm.LpBody.PinStartExpr);
        Assert.Equal("10",  vm.LpBody.PinMaxExpr);
        Assert.Equal("1",   vm.LpBody.PinStepExpr);
        Assert.Equal("3",   vm.LpBody.CompressionExpr);
        Assert.False(vm.LpBody.IsValid);
    }
}
