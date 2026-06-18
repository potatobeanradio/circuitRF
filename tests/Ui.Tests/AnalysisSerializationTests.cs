// Suppress: the sweep fields on HarmonicBalanceAnalysis are [Obsolete] (retained for .cnl read
// compat); these tests specifically verify that legacy round-trip behaviour still works.
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ── Layer 1 gate: shared analysis serializer (polymorphic, framework-free) ────

/// <summary>
/// Verifies that <see cref="AnalysisSerialization"/> round-trips a mixed
/// analyses + measurements list (DC / SP multi-segment / HB with expr fields),
/// and that <c>Id</c> is not present in the serialized JSON.
/// </summary>
public sealed class AnalysisSerializationTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (List<Analysis> Analyses, List<Measurement> Measurements) RoundTrip(
        IReadOnlyList<Analysis>    analyses,
        IReadOnlyList<Measurement> measurements)
    {
        string json = AnalysisSerialization.Serialize(analyses, measurements);
        return AnalysisSerialization.Deserialize(json);
    }

    // ── DC ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DC_RoundTrips()
    {
        var analyses     = new List<Analysis>    { new DcAnalysis("DC1") };
        var measurements = new List<Measurement> { new Measurement("Vout", "V(out)", "V") };

        var (a2, m2) = RoundTrip(analyses, measurements);

        var dc = Assert.IsType<DcAnalysis>(Assert.Single(a2));
        Assert.Equal("DC1", dc.Name);

        var meas = Assert.Single(m2);
        Assert.Equal("Vout", meas.Name);
        Assert.Equal("V(out)", meas.Expression);
        Assert.Equal("V", meas.Unit);
    }

    [Fact]
    public void DC_Json_Contains_No_Id()
    {
        var analyses = new List<Analysis> { new DcAnalysis("DC1") };
        string json  = AnalysisSerialization.Serialize(analyses, []);
        Assert.DoesNotContain("\"Id\"",   json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\"",   json, StringComparison.OrdinalIgnoreCase);
    }

    // ── SP multi-segment ─────────────────────────────────────────────────────

    [Fact]
    public void SP_MultiSegment_RoundTrips()
    {
        var seg1 = new FrequencySpec("1e9", "10e9", "500e6");          // StepSize, Linear
        var seg2 = new FrequencySpec("10e9", "40e9", 61, SweepKind.Log); // PointCount, Log
        var sp   = new SParameterAnalysis("SP1", new[] { seg1, seg2 });

        var (a2, _) = RoundTrip([sp], []);

        var sp2 = Assert.IsType<SParameterAnalysis>(Assert.Single(a2));
        Assert.Equal("SP1", sp2.Name);
        Assert.Equal(2, sp2.Sweeps.Count);

        var s1 = sp2.Sweeps[0];
        Assert.Equal(FreqSpecMode.StepSize,  s1.Mode);
        Assert.Equal(SweepKind.Linear,       s1.Kind);
        Assert.Equal("1e9",                  s1.StartExpr);
        Assert.Equal("10e9",                 s1.StopExpr);
        Assert.Equal("500e6",                s1.StepExpr);
        Assert.Null(s1.NumPoints);

        var s2 = sp2.Sweeps[1];
        Assert.Equal(FreqSpecMode.PointCount, s2.Mode);
        Assert.Equal(SweepKind.Log,           s2.Kind);
        Assert.Equal("10e9",                  s2.StartExpr);
        Assert.Equal("40e9",                  s2.StopExpr);
        Assert.Equal(61,                      s2.NumPoints);
        Assert.Equal("",                      s2.StepExpr);
    }

    [Fact]
    public void SP_ExprFields_Preserved()
    {
        var seg = new FrequencySpec("f0", "2*f0", "step_size");
        var sp  = new SParameterAnalysis("SP_expr", seg);

        var (a2, _) = RoundTrip([sp], []);

        var sp2 = Assert.IsType<SParameterAnalysis>(Assert.Single(a2));
        var s   = sp2.Sweeps[0];
        Assert.Equal("f0",        s.StartExpr);
        Assert.Equal("2*f0",      s.StopExpr);
        Assert.Equal("step_size", s.StepExpr);
    }

    // ── HB with all expr fields ───────────────────────────────────────────────

    [Fact]
    public void HB_AllExprFields_RoundTrip()
    {
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr          = "f0",
            NumFreqsExpr      = "1",
            ToneExprs         = [],
            MaxMixOrderExpr   = "5",
            MaxHarmonicExpr   = "N+2",
            FFTOverSampleExpr = "2",
            TolExpr           = "1e-9",
            DriveSteppingExpr = "IfNecessary",
            GuardHarmonicExpr = "0",
            LambdaExpr        = "0.5",
            MaxIterExpr       = "200",
            SweepVarName      = null,
            SweepStartExpr    = null,
            SweepStopExpr     = null,
            SweepStepExpr     = null,
        };

        var (a2, _) = RoundTrip([hb], []);

        var hb2 = Assert.IsType<HarmonicBalanceAnalysis>(Assert.Single(a2));
        Assert.Equal("HB1",          hb2.Name);
        Assert.Equal("f0",           hb2.ToneExpr);
        Assert.Equal("N+2",          hb2.MaxHarmonicExpr);
        Assert.Equal("2",            hb2.FFTOverSampleExpr);
        Assert.Equal("1e-9",         hb2.TolExpr);
        Assert.Equal("0.5",          hb2.LambdaExpr);
        Assert.Equal("200",          hb2.MaxIterExpr);
        Assert.Null(hb2.SweepVarName);
    }

    [Fact]
    public void HB_MultiTone_ToneExprsPreserved()
    {
        var hb = new HarmonicBalanceAnalysis("HB_mt")
        {
            NumFreqsExpr = "2",
            ToneExprs    = ["f1", "f2"],
            MaxMixOrderExpr = "5",
        };

        var (a2, _) = RoundTrip([hb], []);

        var hb2 = Assert.IsType<HarmonicBalanceAnalysis>(Assert.Single(a2));
        Assert.Equal(2,    hb2.ToneExprs.Length);
        Assert.Equal("f1", hb2.ToneExprs[0]);
        Assert.Equal("f2", hb2.ToneExprs[1]);
    }

    [Fact]
    public void HB_WithSweep_RoundTrips()
    {
        var hb = new HarmonicBalanceAnalysis("HB_sw")
        {
            ToneExpr      = "2e9",
            SweepVarName  = "Pin",
            SweepStartExpr = "-20",
            SweepStopExpr  = "10",
            SweepStepExpr  = "1",
        };

        var (a2, _) = RoundTrip([hb], []);

        var hb2 = Assert.IsType<HarmonicBalanceAnalysis>(Assert.Single(a2));
        Assert.Equal("Pin",  hb2.SweepVarName);
        Assert.Equal("-20",  hb2.SweepStartExpr);
        Assert.Equal("10",   hb2.SweepStopExpr);
        Assert.Equal("1",    hb2.SweepStepExpr);
    }

    // ── Mixed list (the primary gate) ─────────────────────────────────────────

    [Fact]
    public void MixedList_DC_SP_HB_AllRoundTrip()
    {
        var dc   = new DcAnalysis("DC1");
        var sp   = new SParameterAnalysis("SP1",
            new[] { new FrequencySpec("1e9", "10e9", "100e6"), new FrequencySpec("10e9", "40e9", 31) });
        var hb   = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2.45e9", MaxHarmonicExpr = "9" };
        var meas = new Measurement("PAE", "pae(V(out),I(P1))", "%");

        var (a2, m2) = RoundTrip([dc, sp, hb], [meas]);

        Assert.Equal(3, a2.Count);
        Assert.IsType<DcAnalysis>(a2[0]);
        Assert.Equal("DC1", a2[0].Name);

        var sp2 = Assert.IsType<SParameterAnalysis>(a2[1]);
        Assert.Equal("SP1", sp2.Name);
        Assert.Equal(2, sp2.Sweeps.Count);

        var hb2 = Assert.IsType<HarmonicBalanceAnalysis>(a2[2]);
        Assert.Equal("HB1",    hb2.Name);
        Assert.Equal("2.45e9", hb2.ToneExpr);
        Assert.Equal("9",      hb2.MaxHarmonicExpr);

        var meas2 = Assert.Single(m2);
        Assert.Equal("PAE",                   meas2.Name);
        Assert.Equal("pae(V(out),I(P1))",     meas2.Expression);
        Assert.Equal("%",                      meas2.Unit);
    }

    [Fact]
    public void MixedList_Json_Contains_No_Id()
    {
        var dc = new DcAnalysis("DC1");
        var sp = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e9"));
        var hb = new HarmonicBalanceAnalysis("HB1");

        string json = AnalysisSerialization.Serialize([dc, sp, hb], []);

        Assert.DoesNotContain("\"Id\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── DC JSON compactness: no HB/SP fields emitted ─────────────────────────

    [Fact]
    public void DC_Json_OmitsHBAndSPFields()
    {
        var dc   = new DcAnalysis("DC1");
        string json = AnalysisSerialization.Serialize([dc], []);

        // HB-only fields must be absent from DC's JSON
        Assert.DoesNotContain("toneExpr",      json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maxHarmonic",   json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sweeps",        json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Graceful: unknown type tag skipped ────────────────────────────────────

    [Fact]
    public void UnknownTypeTag_IsSkippedGracefully()
    {
        const string json = """
            {
              "analyses": [
                { "type": "loadpull", "name": "LP1" },
                { "type": "dc",       "name": "DC1" }
              ],
              "measurements": []
            }
            """;

        var (a, _) = AnalysisSerialization.Deserialize(json);

        // Loadpull skipped; DC survives.
        var dc = Assert.IsType<DcAnalysis>(Assert.Single(a));
        Assert.Equal("DC1", dc.Name);
    }

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact]
    public void EmptyLists_RoundTrip()
    {
        var (a2, m2) = RoundTrip([], []);
        Assert.Empty(a2);
        Assert.Empty(m2);
    }

    [Fact]
    public void Deserialize_NullOrEmpty_ReturnsEmptyLists()
    {
        var (a1, m1) = AnalysisSerialization.Deserialize("{}");
        Assert.Empty(a1);
        Assert.Empty(m1);

        var (a2, m2) = AnalysisSerialization.Deserialize("null");
        Assert.Empty(a2);
        Assert.Empty(m2);
    }

    // ── .canl (SerializeCanl / DeserializeCanl) ───────────────────────────────

    private static (string Name, string? Description, List<Analysis> Analyses, List<Measurement> Measurements)
        RoundTripCanl(string name, string? description,
                      IReadOnlyList<Analysis> analyses, IReadOnlyList<Measurement> measurements)
    {
        string json = AnalysisSerialization.SerializeCanl(name, description, analyses, measurements);
        return AnalysisSerialization.DeserializeCanl(json);
    }

    [Fact]
    public void Canl_NameAndDescription_RoundTrip()
    {
        var (name, desc, a, _) = RoundTripCanl("My Setup", "Test description", [new DcAnalysis("DC1")], []);
        Assert.Equal("My Setup",         name);
        Assert.Equal("Test description", desc);
        Assert.Single(a);
    }

    [Fact]
    public void Canl_NullDescription_RoundTripsAsNull()
    {
        var (_, desc, _, _) = RoundTripCanl("T", null, [new DcAnalysis("DC1")], []);
        Assert.Null(desc);
    }

    [Fact]
    public void Canl_MixedAnalyses_RoundTrip()
    {
        var sp   = new SParameterAnalysis("SP1", new[] { new FrequencySpec("1e9", "10e9", "500e6") });
        var hb   = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2e9", MaxHarmonicExpr = "5" };
        var meas = new Measurement("Pout", "pout()", "dBm");

        var (_, _, a2, m2) = RoundTripCanl("RF Setup", "A test template", [sp, hb], [meas]);

        Assert.Equal(2, a2.Count);
        Assert.IsType<SParameterAnalysis>(a2[0]);
        Assert.Equal("SP1", a2[0].Name);
        var hb2 = Assert.IsType<HarmonicBalanceAnalysis>(a2[1]);
        Assert.Equal("2e9", hb2.ToneExpr);
        Assert.Equal("Pout", Assert.Single(m2).Name);
    }

    [Fact]
    public void Canl_Json_Contains_No_Id()
    {
        string json = AnalysisSerialization.SerializeCanl("T", null, [new DcAnalysis("DC1")], []);
        Assert.DoesNotContain("\"Id\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canl_AnalysisDtos_MatchClipboardFormat_SameTypes()
    {
        // §5.4: clipboard and .canl must use the same analysis DTOs.
        // Both must deserialize to analyses of the same types and names.
        var dc = new DcAnalysis("DC1");
        var sp = new SParameterAnalysis("SP1", new[] { new FrequencySpec("1e9", "10e9", 101) });

        var (clipA, _)   = AnalysisSerialization.Deserialize(
                               AnalysisSerialization.Serialize([dc, sp], []));
        var (_, _, canlA, _) = AnalysisSerialization.DeserializeCanl(
                                   AnalysisSerialization.SerializeCanl("T", null, [dc, sp], []));

        Assert.Equal(clipA.Count, canlA.Count);
        for (int i = 0; i < clipA.Count; i++)
        {
            Assert.Equal(clipA[i].GetType(), canlA[i].GetType());
            Assert.Equal(clipA[i].Name,      canlA[i].Name);
        }
    }

    // ── ParametricSweep Spec form (brief-sweep-revamp-1-persistence Part B) ───

    [Fact]
    public void SpecSweep_StepSize_Linear_RoundTripsViaSerialize()
    {
        var spec = new SweepSpec(-30.0, 0.0, 0.5, SweepAxisMode.StepSize, SweepKind.Linear);
        var psa  = new ParametricSweepAnalysis("SW1", "Pin", spec, "HB1");

        var (a2, _) = RoundTrip([psa], []);

        var sw = Assert.IsType<ParametricSweepAnalysis>(Assert.Single(a2));
        Assert.NotNull(sw.Spec);
        Assert.Equal(SweepAxisMode.StepSize, sw.Spec!.Mode);
        Assert.Equal(SweepKind.Linear,       sw.Spec.Kind);
        Assert.Equal(-30.0, sw.Spec.Start,       precision: 9);
        Assert.Equal(  0.0, sw.Spec.Stop,        precision: 9);
        Assert.Equal(  0.5, sw.Spec.StepOrCount, precision: 9);
        Assert.True(sw.SweepValues.Length > 0, "SweepValues should be expanded from Spec");
    }

    [Fact]
    public void SpecSweep_PointCount_Log_RoundTripsViaSerialize()
    {
        var spec = new SweepSpec(1.0, 10.0, 11, SweepAxisMode.PointCount, SweepKind.Log);
        var psa  = new ParametricSweepAnalysis("SW2", "Freq", spec, "HB1");

        var (a2, _) = RoundTrip([psa], []);

        var sw = Assert.IsType<ParametricSweepAnalysis>(Assert.Single(a2));
        Assert.NotNull(sw.Spec);
        Assert.Equal(SweepAxisMode.PointCount, sw.Spec!.Mode);
        Assert.Equal(SweepKind.Log,            sw.Spec.Kind);
        Assert.Equal( 1.0, sw.Spec.Start,       precision: 9);
        Assert.Equal(10.0, sw.Spec.Stop,        precision: 9);
        Assert.Equal(11.0, sw.Spec.StepOrCount, precision: 9);
    }

    [Fact]
    public void SpecSweep_Disabled_EnabledPreservedViaSerialize()
    {
        var spec = new SweepSpec(0.0, 1.0, 3, SweepAxisMode.PointCount, SweepKind.Linear);
        var psa  = new ParametricSweepAnalysis("SW_dis", "x", spec, "HB1") { Enabled = false };

        var (a2, _) = RoundTrip([psa], []);

        var sw = Assert.IsType<ParametricSweepAnalysis>(Assert.Single(a2));
        Assert.False(sw.Enabled);
        Assert.NotNull(sw.Spec);
    }

    [Fact]
    public void SpecSweep_ToDto_FromDto_SpecAndEnabledPreserved()
    {
        var spec = new SweepSpec(-20.0, 10.0, 0.25, SweepAxisMode.StepSize, SweepKind.Linear);
        var psa  = new ParametricSweepAnalysis("SW1", "Pin", spec, "HB1") { Enabled = false };

        var dto    = AnalysisSerialization.ToDto(psa);
        var result = AnalysisSerialization.FromDto(dto);

        // DTO should carry Spec fields, not the expanded list
        Assert.Null(dto.PsaValues);
        Assert.Equal(SweepAxisMode.StepSize, dto.PsaMode);
        Assert.Equal(-20.0, dto.PsaStart!.Value,       precision: 9);
        Assert.Equal( 10.0, dto.PsaStop!.Value,        precision: 9);
        Assert.Equal(  0.25, dto.PsaStepOrCount!.Value, precision: 9);
        Assert.Equal(SweepKind.Linear, dto.PsaKind);

        var sw = Assert.IsType<ParametricSweepAnalysis>(result);
        Assert.Equal("SW1",  sw.Name);
        Assert.Equal("Pin",  sw.SweepVarName);
        Assert.Equal("HB1",  sw.InnerAnalysisName);
        Assert.False(sw.Enabled);
        Assert.NotNull(sw.Spec);
        Assert.Equal(SweepAxisMode.StepSize, sw.Spec!.Mode);
        Assert.Equal(SweepKind.Linear,       sw.Spec.Kind);
        Assert.Equal(-20.0, sw.Spec.Start,       precision: 9);
        Assert.Equal( 10.0, sw.Spec.Stop,        precision: 9);
        Assert.Equal( 0.25, sw.Spec.StepOrCount, precision: 9);
    }

    [Fact]
    public void ExplicitListSweep_StillRoundTrips_AsListNotSpec()
    {
        var values = new[] { 1.0, 2.0, 5.0, 10.0 };
        var psa    = new ParametricSweepAnalysis("SW_list", "Vgs", values, "DC1");

        var (a2, _) = RoundTrip([psa], []);

        var sw = Assert.IsType<ParametricSweepAnalysis>(Assert.Single(a2));
        Assert.Null(sw.Spec);
        Assert.Equal(4, sw.SweepValues.Length);
        Assert.Equal( 1.0, sw.SweepValues[0], precision: 9);
        Assert.Equal( 2.0, sw.SweepValues[1], precision: 9);
        Assert.Equal( 5.0, sw.SweepValues[2], precision: 9);
        Assert.Equal(10.0, sw.SweepValues[3], precision: 9);
    }

    [Fact]
    public void SpecSweep_ViaCanl_SpecPreserved()
    {
        var spec = new SweepSpec(-10.0, 10.0, 5, SweepAxisMode.PointCount, SweepKind.Linear);
        var psa  = new ParametricSweepAnalysis("SW_canl", "Pin", spec, "HB1");

        var (_, _, a2, _) = RoundTripCanl("T", null, [psa], []);

        var sw = Assert.IsType<ParametricSweepAnalysis>(Assert.Single(a2));
        Assert.NotNull(sw.Spec);
        Assert.Equal(SweepAxisMode.PointCount, sw.Spec!.Mode);
        Assert.Equal(-10.0, sw.Spec.Start,       precision: 9);
        Assert.Equal( 10.0, sw.Spec.Stop,        precision: 9);
        Assert.Equal(  5.0, sw.Spec.StepOrCount, precision: 9);
    }
}

// ── Layer 2 gate: .csch round-trip + SchematicHasAnalyses ────────────────────

/// <summary>
/// Verifies that <see cref="SchematicPersistence"/> round-trips analyses+measurements
/// through the .csch format via the shared encoder, that old .csch files (no analyses
/// key) load as empty, and that <c>SchematicHasAnalyses</c> drives <c>IsTestBench</c>.
/// </summary>
public sealed class CschAnalysisRoundTripTests
{
    private static SchematicEditModel BuildModelWithAnalyses()
    {
        var m = new SchematicEditModel { GridSize = 100 };

        m.Analyses.Add(new DcAnalysis("DC1"));
        m.Analyses.Add(new SParameterAnalysis("SP1",
            new[] { new FrequencySpec("1e9", "10e9", "500e6") }));
        m.Analyses.Add(new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2e9", MaxHarmonicExpr = "7" });
        m.Measurements.Add(new Measurement("Pout", "P(V_out)", "dBm"));

        return m;
    }

    // ── .csch round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void CschFile_WithAnalyses_RoundTripsViaSharedEncoder()
    {
        var original = BuildModelWithAnalyses();
        string json  = SchematicPersistence.Serialize(original);
        var (restored, _, _) = SchematicPersistence.Deserialize(json);

        Assert.Equal(3, restored.Analyses.Count);

        Assert.IsType<DcAnalysis>(restored.Analyses[0]);
        Assert.Equal("DC1", restored.Analyses[0].Name);

        var sp = Assert.IsType<SParameterAnalysis>(restored.Analyses[1]);
        Assert.Equal("SP1", sp.Name);
        Assert.Single(sp.Sweeps);
        Assert.Equal("1e9",   sp.Sweeps[0].StartExpr);
        Assert.Equal("500e6", sp.Sweeps[0].StepExpr);

        var hb = Assert.IsType<HarmonicBalanceAnalysis>(restored.Analyses[2]);
        Assert.Equal("HB1", hb.Name);
        Assert.Equal("2e9", hb.ToneExpr);
        Assert.Equal("7",   hb.MaxHarmonicExpr);

        var m = Assert.Single(restored.Measurements);
        Assert.Equal("Pout",   m.Name);
        Assert.Equal("P(V_out)", m.Expression);
        Assert.Equal("dBm",    m.Unit);
    }

    // ── Graceful load: old .csch (no analyses key) ────────────────────────────

    [Fact]
    public void OldCschFile_NoAnalysesKey_LoadsAsEmpty()
    {
        const string oldJson = """
            {
              "formatVersion": 2,
              "cellName": "old_cell",
              "gridSize": 100.0,
              "gridSnap": true,
              "authorGridDivisor": 20,
              "components": [],
              "wires": [],
              "netLabels": [],
              "dots": [],
              "canvasObjects": [],
              "view": { "panX": 0, "panY": 0, "zoom": 1 }
            }
            """;

        var (m, _, _) = SchematicPersistence.Deserialize(oldJson);

        Assert.Empty(m.Analyses);
        Assert.Empty(m.Measurements);
    }

    // ── Analysis-free .csch omits analyses key ────────────────────────────────

    [Fact]
    public void EmptyAnalyses_NotWrittenToJson()
    {
        var m    = new SchematicEditModel();   // no analyses
        string json = SchematicPersistence.Serialize(m);

        Assert.DoesNotContain("\"analyses\"",    json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"measurements\"",json, StringComparison.OrdinalIgnoreCase);
    }

    // ── SchematicHasAnalyses → IsTestBench ───────────────────────────────────

    [Fact]
    public void SchematicWithAnalyses_CellStep_IsTestBench()
    {
        var model = BuildModelWithAnalyses();
        var doc   = new SchematicDocument("TestBench-1", new SchematicViewModel(model));

        var builder = new SavePlanBuilder(null, "/tmp", [doc]);
        var plan    = builder.Build();

        var cellStep = Assert.Single(plan.CellSteps);
        Assert.True(cellStep.IsTestBench);
    }

    [Fact]
    public void SchematicWithoutAnalyses_CellStep_IsNotTestBench()
    {
        var model = new SchematicEditModel();  // no analyses
        var doc   = new SchematicDocument("Cell-1", new SchematicViewModel(model));

        var builder = new SavePlanBuilder(null, "/tmp", [doc]);
        var plan    = builder.Build();

        var cellStep = Assert.Single(plan.CellSteps);
        Assert.False(cellStep.IsTestBench);
    }
}
