using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for brief-cell-first-and-ui-fixes.md §4 (R-cc-4/4a/4b/4c/4d): the S-parameter/HB
/// analysis-list card frequency display. <see cref="AnalysisRowViewModel.FormatFreq"/> (deleted)
/// parsed the raw coefficient string as if it were already hertz, ignoring the unit dropdown
/// entirely — a 1–10 GHz sweep displayed as "1 Hz–10 Hz". The fix routes the card's own summary
/// through <see cref="AnalysisPreviewHelper"/>'s shared frequency resolution (the same one
/// <c>ComputeFreqPreview</c> already used for the Add/Edit Analysis dialog's inline hint),
/// preserving var-unit-wins and mixed-unit-compound resolution.
/// </summary>
public class AnalysisFreqCardSummaryTests
{
    private static SchematicEditModel Model() => new SchematicEditModel();

    private static SchematicEditModel ModelWithVar(string varName, string expression, string unit)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.Var, X = 0, Y = 0, InstanceName = "VAR1" };
        comp.Parameters.Add(new EditableParameter { Name = varName, Expression = expression, Unit = unit });
        model.Components.Add(comp);
        return model;
    }

    private static SchematicEditModel ModelWithVars((string name, string expr, string unit)[] vars)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.Var, X = 0, Y = 0, InstanceName = "VAR1" };
        foreach (var (n, e, u) in vars)
            comp.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = u });
        model.Components.Add(comp);
        return model;
    }

    private static AnalysisRowViewModel Row(Analysis a, SchematicEditModel model)
        => new(a, new SchematicViewModel(model));

    // ── Gate 6: SI-suffixed direct literal display, all three sub-Hz-and-above units ──────────────

    [Fact]
    public void ComputeFreqSummary_OneToTenGHz_DisplaysWithGHzSuffix()
    {
        var model = Model();
        Assert.Equal("1 GHz", AnalysisPreviewHelper.ComputeFreqSummary("1", "GHz", model));
        Assert.Equal("10 GHz", AnalysisPreviewHelper.ComputeFreqSummary("10", "GHz", model));
    }

    [Fact]
    public void ComputeFreqSummary_MHzAndKHz_DisplayCorrectly()
    {
        var model = Model();
        Assert.Equal("500 MHz", AnalysisPreviewHelper.ComputeFreqSummary("500", "MHz", model));
        Assert.Equal("2.5 MHz", AnalysisPreviewHelper.ComputeFreqSummary("2.5", "MHz", model));
        Assert.Equal("100 kHz", AnalysisPreviewHelper.ComputeFreqSummary("100", "kHz", model));
    }

    [Fact]
    public void ComputeFreqSummary_BareHzLiteral_StillDisplays_UnlikeThePreviewsSuppression()
    {
        // ComputeFreqPreview suppresses a bare Hz literal (no "= 2.4" editor noise); the card must
        // NOT inherit that suppression — a plain 1,000,000 Hz field must still read "1 MHz".
        var model = Model();
        Assert.Equal("1 MHz", AnalysisPreviewHelper.ComputeFreqSummary("1000000", "Hz", model));
    }

    // ── Gate 6a: var-unit-wins ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeFreqSummary_VarUnitWins_NotDoubleScaled()
    {
        // RFfreq declared in its own GHz; field dropdown reads MHz. The naive "multiply by the
        // dropdown factor" fix would scale twice (or scale by the wrong factor); this must resolve
        // to exactly 2 GHz, once.
        var model  = ModelWithVar("RFfreq", "2", "GHz");
        Assert.Equal("2 GHz", AnalysisPreviewHelper.ComputeFreqSummary("RFfreq", "MHz", model));
    }

    // ── Gate 6b: mixed-unit compound resolves per-term ──────────────────────────────────────────

    [Fact]
    public void ComputeFreqSummary_MixedUnitCompound_ResolvesPerTerm()
    {
        var model = ModelWithVars([("RFfreq", "2", "GHz"), ("Voff", "100", "MHz")]);
        // 2 GHz + 100 MHz = 2.1 GHz — a single shared multiplier would get this wrong.
        Assert.Equal("2.1 GHz", AnalysisPreviewHelper.ComputeFreqSummary("RFfreq + Voff", "GHz", model));
    }

    // ── Gate 6d: unresolved name ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeFreqSummary_UnresolvedName_ShowsUnknown_NotRawText()
    {
        var model = Model();
        Assert.Equal("unknown: Nope", AnalysisPreviewHelper.ComputeFreqSummary("Nope", "GHz", model));
    }

    // ── End-to-end through AnalysisRowViewModel.Summary — SP and HB, matching gate 6/6a/6d exactly ─

    [Fact]
    public void SpAnalysisCard_OneToTenGHzSweep_SummaryReadsOneToTenGHz()
    {
        var model = Model();
        var sp = new SParameterAnalysis("SP1", new FrequencySpec("1", "10", 101,
            startUnit: "GHz", stopUnit: "GHz"));

        Assert.Equal("1 GHz–10 GHz", Row(sp, model).Summary);
    }

    [Fact]
    public void SpAnalysisCard_MultiSegment_AppendsSegmentCount()
    {
        var model = Model();
        var sp = new SParameterAnalysis("SP1", new List<FrequencySpec>
        {
            new("1", "5", 41, startUnit: "GHz", stopUnit: "GHz"),
            new("5", "10", 51, startUnit: "GHz", stopUnit: "GHz"),
        });

        Assert.Equal("1 GHz–5 GHz, 2 segments", Row(sp, model).Summary);
    }

    [Fact]
    public void SpAnalysisCard_UnresolvedName_ShowsUnknown()
    {
        var model = Model();
        var sp = new SParameterAnalysis("SP1", new FrequencySpec("Nope", "10", 101,
            startUnit: "GHz", stopUnit: "GHz"));

        Assert.Equal("unknown: Nope–10 GHz", Row(sp, model).Summary);
    }

    [Fact]
    public void HbAnalysisCard_ToneUnit_IsHonoredNotIgnored()
    {
        // Root-cause regression: the OLD FormatHbSummary called FormatFreq(hb.ToneExpr) directly,
        // never even reading hb.ToneUnit — a "2" + "GHz" tone displayed as "2 Hz".
        var model = Model();
        var hb = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2", ToneUnit = "GHz", MaxHarmonicExpr = "7" };

        Assert.Equal("f₀=2 GHz, 7 harmonics", Row(hb, model).Summary);
    }

    [Fact]
    public void HbAnalysisCard_ZeroToneSentinel_StillShowsQuestionMark()
    {
        var model = Model();
        var hb = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "0", ToneUnit = "Hz", MaxHarmonicExpr = "7" };

        Assert.Equal("f₀=?, 7 harmonics", Row(hb, model).Summary);
    }

    // ── Multi-tone: the card names EVERY fundamental, not just tone 1 ───────────────────────────

    /// <summary>
    /// A multi-tone HB analysis mirrors tone 1 into the scalar <c>ToneExpr</c> and carries the real
    /// tone set in <c>ToneExprs</c>. The card read only the scalar, so a two-tone run displayed a
    /// single "f₀" and the second fundamental was nowhere on screen (owner-reported).
    /// </summary>
    [Fact]
    public void HbAnalysisCard_MultiTone_ListsEveryTone()
    {
        var model = Model();
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2", ToneUnit = "GHz",          // the mirror of tone 1
            NumFreqsExpr = "2",
            ToneExprs = ["2", "2.4"],
            ToneUnits = ["GHz", "GHz"],
            MaxHarmonicExpr = "7",
        };

        Assert.Equal("f₁=2 GHz, f₂=2.4 GHz, 7 harmonics", Row(hb, model).Summary);
    }

    /// <summary>Each tone resolves through its OWN unit, not tone 1's.</summary>
    [Fact]
    public void HbAnalysisCard_MultiTone_ResolvesEachToneInItsOwnUnit()
    {
        var model = Model();
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2", ToneUnit = "GHz",
            NumFreqsExpr = "3",
            ToneExprs = ["2", "10", "100"],
            ToneUnits = ["GHz", "MHz", "kHz"],
            MaxHarmonicExpr = "5",
        };

        Assert.Equal("f₁=2 GHz, f₂=10 MHz, f₃=100 kHz, 5 harmonics", Row(hb, model).Summary);
    }

    /// <summary>
    /// The card's multi-tone test is <c>HbEngine</c>'s own: <c>NumFreqs &gt; 1</c> AND enough
    /// entries to satisfy it. A declared count the tone list cannot cover is what the engine runs
    /// as single-tone, so the card must say the same rather than invent a tone set.
    /// </summary>
    [Fact]
    public void HbAnalysisCard_NumFreqsWithoutEnoughTones_FallsBackToSingleTone()
    {
        var model = Model();
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2", ToneUnit = "GHz",
            NumFreqsExpr = "3",
            ToneExprs = ["2", "2.001"],
            ToneUnits = ["GHz", "GHz"],
            MaxHarmonicExpr = "7",
        };

        Assert.Equal("f₀=2 GHz, 7 harmonics", Row(hb, model).Summary);
    }

    /// <summary>Single-tone still says f₀ — the multi-tone path must not capture it.</summary>
    [Fact]
    public void HbAnalysisCard_SingleTone_KeepsTheF0Spelling()
    {
        var model = Model();
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2", ToneUnit = "GHz", NumFreqsExpr = "1", MaxHarmonicExpr = "7",
        };

        Assert.Equal("f₀=2 GHz, 7 harmonics", Row(hb, model).Summary);
    }

    // ── Gate 6c: the parametric-sweep summary path is untouched ─────────────────────────────────

    [Fact]
    public void ParametricSweepCard_SummaryUnaffected_ByTheFrequencyCardFix()
    {
        var model = Model();
        var psa = new ParametricSweepAnalysis("SW1", "Vgs", [1.0, 2.0, 3.0, 4.0, 5.0], "dc");

        Assert.Equal("5 pts: 1…5", Row(psa, model).Summary);
    }
}
