using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 1 gate — NetExtractor carries enabled analyses + measurements.
/// Brief §L1: enabled filter, measurements, SP multi-segment.
/// </summary>
public sealed class NetExtractorAnalysesTests
{
    // ── Test 1: only enabled analyses are carried ──────────────────────────────

    [Fact]
    public void Extract_OnlyEnabledAnalyses_Carried()
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
        a2.Enabled = false;   // disabled — must NOT be carried

        model.Analyses.Add(a1);
        model.Analyses.Add(a2);

        var result = NetExtractor.Extract(model);

        Assert.Single(result.TestBench.Analyses);
        Assert.IsType<SParameterAnalysis>(result.TestBench.Analyses[0]);
        Assert.Equal("SP1", result.TestBench.Analyses[0].Name);
    }

    // ── Test 2: disabled-only model yields no analyses ─────────────────────────

    [Fact]
    public void Extract_AllDisabled_NoAnalysesCarried()
    {
        var model = new SchematicEditModel();
        var dc = new DcAnalysis("DC1");
        dc.Enabled = false;
        model.Analyses.Add(dc);

        var result = NetExtractor.Extract(model);

        Assert.Empty(result.TestBench.Analyses);
    }

    // ── Test 3: measurements carried regardless of analyses ───────────────────

    [Fact]
    public void Extract_Measurements_AllCarried()
    {
        var model = new SchematicEditModel();
        model.Measurements.Add(new Measurement("Pout", "pout()", "dBm"));
        model.Measurements.Add(new Measurement("Gain", "S21", "dB"));

        var result = NetExtractor.Extract(model);

        Assert.Equal(2, result.TestBench.Measurements.Count);
        Assert.Contains(result.TestBench.Measurements, m => m.Name == "Pout" && m.Unit == "dBm");
        Assert.Contains(result.TestBench.Measurements, m => m.Name == "Gain" && m.Unit == "dB");
    }

    // ── Test 4: SP multi-segment carried with all segments intact ─────────────

    [Fact]
    public void Extract_SpMultiSegment_BothSegmentsCarried()
    {
        var model = new SchematicEditModel();
        var sp = new SParameterAnalysis("SP1",
            new List<FrequencySpec>
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
            new List<FrequencySpec>
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
}
