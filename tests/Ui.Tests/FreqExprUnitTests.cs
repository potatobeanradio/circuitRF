using System.Numerics;
using System.Reflection;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for frequency expression + unit in the VM/serialization layer
/// (brief-analysis-freq-expr-unit.md, Tests 9-10 and brief-analysis-freq-preview-units.md, Tests 1-7).
/// </summary>
public class FreqExprUnitTests
{
    private static SchematicEditModel Model() => new SchematicEditModel();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SchematicEditModel ModelWithVar(string varName, string expression, string unit)
        => ModelWithVars([(varName, expression, unit)]);

    private static SchematicEditModel ModelWithVars((string name, string expr, string unit)[] vars)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.Var, X = 0, Y = 0, InstanceName = "VAR1" };
        foreach (var (n, e, u) in vars)
            comp.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = u });
        model.Components.Add(comp);
        return model;
    }

    // ── Brief brief-analysis-freq-preview-units.md gate tests ────────────────

    // Note: these previews are now EXACT, so the prefix is an honest "=" (not "≈"). The resolved
    // values are unchanged — only the prefix reflects that the displayed digits are lossless.

    [Fact]
    public void FreqPreview_VarUnitWins_DifferentFieldUnit()
    {
        // RFfreq=2 (GHz by its unit); field unit is MHz.
        // Var-unit-wins → 2 * 1e9 = 2e9, NOT 2 * 1e6 = 2e6.
        var model  = ModelWithVar("RFfreq", "2", "GHz");
        var result = AnalysisPreviewHelper.ComputeFreqPreview("RFfreq", "MHz", model);
        Assert.Equal("= 2E+09", result);
    }

    [Fact]
    public void FreqPreview_VarUnitWins_SameFieldUnit()
    {
        // RFfreq=2 GHz; field unit also GHz → 2 * 1e9.
        var model  = ModelWithVar("RFfreq", "2", "GHz");
        var result = AnalysisPreviewHelper.ComputeFreqPreview("RFfreq", "GHz", model);
        Assert.Equal("= 2E+09", result);
    }

    [Fact]
    public void FreqPreview_UnitlessVar_FieldApplies()
    {
        // RFfreq=2, no unit; field unit GHz applies → 2 * 1e9.
        var model  = ModelWithVar("RFfreq", "2", "");
        var result = AnalysisPreviewHelper.ComputeFreqPreview("RFfreq", "GHz", model);
        Assert.Equal("= 2E+09", result);
    }

    [Fact]
    public void FreqPreview_NumericShowsWithUnit()
    {
        var model = Model();
        // Bare number + GHz → show preview (confirms multiplier applied).
        Assert.Equal("= 2.4E+09", AnalysisPreviewHelper.ComputeFreqPreview("2.4", "GHz", model));
        // Bare number + Hz → suppress (no "= 2.4" noise).
        Assert.Equal("", AnalysisPreviewHelper.ComputeFreqPreview("2.4", "Hz", model));
    }

    [Fact]
    public void FreqPreview_Unknown()
    {
        // An unresolved name reports the missing name with no value prefix (it is not a value).
        var model  = Model();
        var result = AnalysisPreviewHelper.ComputeFreqPreview("Nope", "GHz", model);
        Assert.Equal("unknown: Nope", result);
    }

    [Fact]
    public void FreqPreview_Compound_Homogeneous()
    {
        // 2*RFfreq where RFfreq=2 GHz: value=4, var-unit=GHz → 4 * 1e9 = 4e9.
        var model  = ModelWithVar("RFfreq", "2", "GHz");
        var result = AnalysisPreviewHelper.ComputeFreqPreview("2*RFfreq", "MHz", model);
        Assert.Equal("= 4E+09", result);
    }

    // Mixed-unit compound: RFfreq (GHz) + Voff (MHz) — each reference applies its OWN unit, so the
    // result is EXACT (2e9 + 100e6 = 2.1e9), not the old single-multiplier approximation.
    [Fact]
    public void FreqPreview_Compound_MixedUnits_Exact()
    {
        var model  = ModelWithVars([("RFfreq", "2", "GHz"), ("Voff", "100", "MHz")]);
        var result = AnalysisPreviewHelper.ComputeFreqPreview("RFfreq + Voff", "GHz", model);
        Assert.Equal("= 2.1E+09", result);
    }

    // A value that needs more than the display budget stays honestly approximate ("≈").
    [Fact]
    public void Preview_RoundedValue_StaysApproximate()
    {
        var model  = ModelWithVar("X", "1.23456789", "");
        var result = AnalysisPreviewHelper.ComputePreview("X", model);
        Assert.StartsWith("≈ ", result);
    }

    // A clean value resolves to an honest "=".
    [Fact]
    public void Preview_ExactValue_UsesEquals()
    {
        var model  = ModelWithVar("X", "2.5", "");
        var result = AnalysisPreviewHelper.ComputePreview("3*X", model);
        Assert.Equal("= 7.5", result);
    }

    // Unit-bearing reference resolves to its base value (pF → 1e-12), exactly.
    [Fact]
    public void Preview_UnitBearingVar_ResolvesToBaseUnit()
    {
        var model  = ModelWithVar("Cval", "1", "pF");
        var result = AnalysisPreviewHelper.ComputePreview("2*Cval", model);
        Assert.Equal("= 2E-12", result);
    }

    // ── FormatValueHonest: the §9.1 prefix the component-instance parameter editor now uses ──
    // (The component parameter editor previously hardcoded "≈"; it now shares this honest formatter.)

    [Fact]
    public void FormatValueHonest_ExactReal_UsesEquals()
        => Assert.Equal("= 2E+09", AnalysisPreviewHelper.FormatValueHonest(new Value(2e9)));

    [Fact]
    public void FormatValueHonest_RoundedReal_StaysApproximate()
        => Assert.StartsWith("≈ ", AnalysisPreviewHelper.FormatValueHonest(new Value(1.0 / 3.0)));

    [Fact]
    public void FormatValueHonest_ExactComplex_UsesEquals()
        => Assert.StartsWith("= ", AnalysisPreviewHelper.FormatValueHonest(new Value(new Complex(50, -25))));

    [Fact]
    public void FormatValueHonest_NonScalar_IsEmpty()
        => Assert.Equal("", AnalysisPreviewHelper.FormatValueHonest(new Value(true)));

    [Fact]
    public void No_ToHzExpr_Callers()
    {
        // ToHzExpr was deleted in brief-analysis-freq-preview-units.md Part D.
        // Guard: verify the method no longer exists on FreqUnitHelper.
        var method = typeof(FreqUnitHelper).GetMethod(
            "ToHzExpr",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(string), typeof(string)]);
        Assert.Null(method);
    }

    // ── T9: HbBodyViewModel round-trips raw coeff+unit ───────────────────────

    [Fact]
    public void HbBody_RoundTrip_KeepsCoeffAndUnit()
    {
        // Build an HB analysis with symbolic tone expression.
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "RFfreq",
            ToneUnit = "GHz",
            MaxHarmonicExpr = "7",
        };

        var vm = HbBodyViewModel.FromAnalysis(hba, Model());

        // VM should expose raw coeff + unit (no baking).
        Assert.Equal("RFfreq", vm.ToneCoeff);
        Assert.Equal("GHz",    vm.ToneUnit);

        // Building back should preserve them.
        var built = vm.BuildAnalysis("HB1", enabled: true);
        Assert.Equal("RFfreq", built.ToneExpr);
        Assert.Equal("GHz",    built.ToneUnit);
    }

    [Fact]
    public void HbBody_RoundTrip_LegacyNumericSplit()
    {
        // Old-style baked numeric (ToneUnit="Hz", ToneExpr="2.4e9") → legacy-nicety Split
        // should give a pretty display ("2.4" + "GHz") after FromAnalysis.
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2.4e9",
            ToneUnit = "Hz",
            MaxHarmonicExpr = "7",
        };

        var vm = HbBodyViewModel.FromAnalysis(hba, Model());

        // FreqUnitHelper.Split("2.4e9") → ("2.4", "GHz").
        Assert.Equal("2.4", vm.ToneCoeff);
        Assert.Equal("GHz", vm.ToneUnit);
    }

    [Fact]
    public void HbBody_RoundTrip_MultiTone_KeepsUnits()
    {
        // Multi-tone with explicit units on both tones.
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExprs    = ["f1", "f2"],
            ToneUnits    = ["GHz", "MHz"],
            NumFreqsExpr = "2",
            MaxMixOrderExpr = "3",
        };

        var vm = HbBodyViewModel.FromAnalysis(hba, Model());
        Assert.True(vm.MultiTone);
        Assert.Equal("f2",  vm.Tone2Coeff);
        Assert.Equal("MHz", vm.Tone2Unit);

        var built = vm.BuildAnalysis("HB1", enabled: true);
        Assert.Equal("f2",  built.ToneExprs[1]);
        Assert.Equal("MHz", built.ToneUnits[1]);
    }

    // Regression: with Multi enabled, Tone 1 must survive Build → reopen (the dialog-OK bug where
    // Tone 1 was written only to ToneExprs[0] but read back from the scalar ToneExpr default "0").
    // Uses a var/expression tone to confirm expressions round-trip, not just numbers.
    [Fact]
    public void HbBody_MultiTone_RoundTrip_KeepsTone1()
    {
        var vm = new HbBodyViewModel(Model())
        {
            MultiTone = true,
            ToneCoeff = "RFf1", ToneUnit = "GHz",
            Tone2Coeff = "RFf2", Tone2Unit = "GHz",
        };

        var built = vm.BuildAnalysis("HB1", enabled: true);
        // Tone 1 lands in BOTH the engine's multi-tone slot and the scalar field.
        Assert.Equal("RFf1", built.ToneExprs[0]);
        Assert.Equal("RFf1", built.ToneExpr);
        Assert.Equal("RFf2", built.ToneExprs[1]);

        // Reopening the dialog preserves both tones (previously Tone 1 became "0").
        var vm2 = HbBodyViewModel.FromAnalysis(built, Model());
        Assert.True(vm2.MultiTone);
        Assert.Equal("RFf1", vm2.ToneCoeff);
        Assert.Equal("RFf2", vm2.Tone2Coeff);
    }

    // A legacy/engine-shaped multi-tone analysis (ToneExprs set, scalar ToneExpr left at "0") must
    // still load Tone 1 from ToneExprs[0].
    [Fact]
    public void HbBody_MultiTone_LoadsTone1_FromToneExprs()
    {
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExprs    = ["fa", "fb"],
            ToneUnits    = ["GHz", "GHz"],
            NumFreqsExpr = "2",
        };
        var vm = HbBodyViewModel.FromAnalysis(hba, Model());
        Assert.Equal("fa", vm.ToneCoeff);   // not "0"
        Assert.Equal("fb", vm.Tone2Coeff);
    }

    // ── T10: AnalysisSerialization round-trips units ─────────────────────────

    [Fact]
    public void AnalysisSerialization_HbRoundTrip_Units()
    {
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "RFfreq",
            ToneUnit = "GHz",
        };

        var dto  = AnalysisSerialization.ToDto(hba);
        Assert.Equal("GHz", dto.ToneUnit);

        var back = (HarmonicBalanceAnalysis)AnalysisSerialization.FromDto(dto)!;
        Assert.Equal("RFfreq", back.ToneExpr);
        Assert.Equal("GHz",    back.ToneUnit);
    }

    [Fact]
    public void AnalysisSerialization_HbRoundTrip_UnitHz_Omitted()
    {
        // ToneUnit="Hz" should be omitted from the DTO (back-compat: absent → "Hz").
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2.4e9",
            ToneUnit = "Hz",
        };

        var dto = AnalysisSerialization.ToDto(hba);
        Assert.Null(dto.ToneUnit);   // Hz is omitted to stay backward-compatible

        var back = (HarmonicBalanceAnalysis)AnalysisSerialization.FromDto(dto)!;
        Assert.Equal("Hz", back.ToneUnit);
    }

    [Fact]
    public void AnalysisSerialization_SParamRoundTrip_Units()
    {
        var freq = new FrequencySpec("f_low", "f_high", 101, SweepKind.Linear, "GHz", "GHz");
        var spa  = new SParameterAnalysis("SP1", freq);

        var dto = AnalysisSerialization.ToDto(spa);
        Assert.Equal("GHz", dto.Sweeps![0].StartUnit);
        Assert.Equal("GHz", dto.Sweeps![0].StopUnit);

        var back = (SParameterAnalysis)AnalysisSerialization.FromDto(dto)!;
        Assert.Equal("f_low",  back.Sweeps[0].StartExpr);
        Assert.Equal("GHz",    back.Sweeps[0].StartUnit);
        Assert.Equal("f_high", back.Sweeps[0].StopExpr);
        Assert.Equal("GHz",    back.Sweeps[0].StopUnit);
    }
}
