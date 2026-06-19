using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 1 gate — NetExtractor carries ALL analyses into TestBench.Analyses
/// (so ParametricSweepEngine can find inner analyses by name).
/// Dispatch filtering (Enabled=true only) happens in SchematicRunService, not here.
/// </summary>
public sealed class NetExtractorAnalysesTests
{
    // ── Test 1: all analyses (including disabled) are carried ─────────────────

    [Fact]
    public void Extract_AllAnalysesCarried_IncludingDisabled()
    {
        var model = new SchematicEditModel();
        var a1 = new SParameterAnalysis("SP1",
            new FrequencySpec("1e9", "5e9", 51));
        a1.Enabled = true;

        var a2 = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "1e9",
            MaxHarmonicExpr = "7",
        };
        a2.Enabled = false; // disabled — now still carried for chain lookup

        model.Analyses.Add(a1);
        model.Analyses.Add(a2);

        var result = NetExtractor.Extract(model);

        // Both analyses are in the TestBench now (the run service filters Enabled at dispatch).
        Assert.Equal(2, result.TestBench.Analyses.Count);
        Assert.Contains(result.TestBench.Analyses, a => a.Name == "SP1");
        Assert.Contains(result.TestBench.Analyses, a => a.Name == "HB1");
    }

    // ── Test 2: disabled-only model → all analyses still carried ──────────────

    [Fact]
    public void Extract_AllDisabled_AllAnalysesCarried()
    {
        var model = new SchematicEditModel();
        var dc = new DcAnalysis("DC1");
        dc.Enabled = false;
        model.Analyses.Add(dc);

        var result = NetExtractor.Extract(model);

        // Now carried (but SchematicRunService will skip it when dispatching).
        Assert.Single(result.TestBench.Analyses);
        Assert.Equal("DC1", result.TestBench.Analyses[0].Name);
    }

    // ── Test 3: MEAS component rows carried into tb.Measurements ─────────────

    [Fact]
    public void Extract_Measurements_AllCarried()
    {
        // Measurements now come from MEAS components, not model.Measurements directly.
        var model = new SchematicEditModel();
        var measComp = new EditableComponent { Symbol = SymbolKind.Meas, InstanceName = "MEAS1" };
        measComp.Parameters.Add(new EditableParameter { Name = "Pout", Expression = "pout()", Unit = "dBm" });
        measComp.Parameters.Add(new EditableParameter { Name = "Gain", Expression = "S21", Unit = "dB" });
        model.Components.Add(measComp);

        var result = NetExtractor.Extract(model);

        Assert.Equal(2, result.TestBench.Measurements.Count);
        Assert.Contains(result.TestBench.Measurements, m => m.Name == "Pout");
        Assert.Contains(result.TestBench.Measurements, m => m.Name == "Gain");
    }

    // ── Test 4: SP multi-segment carried with all segments intact ─────────────

    [Fact]
    public void Extract_SpMultiSegment_BothSegmentsCarried()
    {
        var model = new SchematicEditModel();
        var sp = new SParameterAnalysis("SP1",
            new System.Collections.Generic.List<FrequencySpec>
            {
                new FrequencySpec("1e9", "5e9", 41),
                new FrequencySpec("5e9", "10e9", 51),
            });

        model.Analyses.Add(sp);

        var result = NetExtractor.Extract(model);

        var carried = Assert.Single(result.TestBench.Analyses) as SParameterAnalysis;
        Assert.NotNull(carried);
        Assert.Equal(2, carried.Sweeps.Count);
        Assert.Equal("1e9",  carried.Sweeps[0].StartExpr);
        Assert.Equal("5e9",  carried.Sweeps[0].StopExpr);
        Assert.Equal("5e9",  carried.Sweeps[1].StartExpr);
        Assert.Equal("10e9", carried.Sweeps[1].StopExpr);
    }

    // ── Test 5: SP multi-segment expands to unioned freq points ───────────────

    [Fact]
    public void Extract_SpMultiSegment_ExpandYieldsUnionedFreqs()
    {
        var model = new SchematicEditModel();
        // 2 pts in seg1 [1e9, 2e9], 2 pts in seg2 [2e9, 3e9] → union = [1e9, 2e9, 3e9]
        var sp = new SParameterAnalysis("SP1",
            new System.Collections.Generic.List<FrequencySpec>
            {
                new FrequencySpec("1e9", "2e9", 2),   // PointCount: [1e9, 2e9]
                new FrequencySpec("2e9", "3e9", 2),   // PointCount: [2e9, 3e9]
            });
        model.Analyses.Add(sp);

        var result = NetExtractor.Extract(model);

        var carried = (SParameterAnalysis)result.TestBench.Analyses[0];
        var freqs = carried.Expand();

        // Union dedup: 2e9 appears in both segments but only once in the sorted set.
        Assert.Equal(3, freqs.Length);
        Assert.Equal(1e9, freqs[0], 1.0);
        Assert.Equal(2e9, freqs[1], 1.0);
        Assert.Equal(3e9, freqs[2], 1.0);
    }

    // ── Test 6: ParametricSweepAnalysis chain is fully carried ───────────────

    [Fact]
    public void Extract_SweepChain_AllMembersCarried()
    {
        var model = new SchematicEditModel();

        var hb = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "1e9", MaxHarmonicExpr = "7" };
        hb.Enabled = false;
        var sweep = new ParametricSweepAnalysis("HB1_sweep_Pavl", "Pavl",
            new double[] { -20, -15, -10, -5, 0 }, "HB1");
        sweep.Enabled = true;

        model.Analyses.Add(hb);
        model.Analyses.Add(sweep);

        var result = NetExtractor.Extract(model);

        Assert.Equal(2, result.TestBench.Analyses.Count);
        Assert.Contains(result.TestBench.Analyses, a => a.Name == "HB1");
        Assert.Contains(result.TestBench.Analyses, a => a.Name == "HB1_sweep_Pavl");
    }
}
