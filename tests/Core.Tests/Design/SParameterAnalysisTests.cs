using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Design;

/// <summary>
/// Layer 2 gate: SParameterAnalysis — multi-segment sweeps, whole-analysis Expand(),
/// CnlReader typed promotion, single-segment regression.
/// </summary>
public sealed class SParameterAnalysisTests
{
    // ── Single-segment regression ─────────────────────────────────────────────

    [Fact]
    public void SingleSegment_ExpandsIdenticallyToFrequencySpec()
    {
        // A single-segment analysis must produce the same array as FrequencySpec.Expand().
        var spec = new FrequencySpec(1e9, 10e9, 1e9);
        var spa  = new SParameterAnalysis("SP", spec);

        var fromSpec  = spec.Expand();
        var fromSpa   = spa.Expand();

        Assert.Equal(fromSpec.Length, fromSpa.Length);
        for (int i = 0; i < fromSpec.Length; i++)
            Assert.Equal(fromSpec[i], fromSpa[i], 1.0);
    }

    [Fact]
    public void SingleSegment_FreqAccessorReturnsSameSegment()
    {
        var spec = new FrequencySpec("1e9", "5e9", 5);
        var spa  = new SParameterAnalysis("mysp", spec);
        Assert.Single(spa.Sweeps);
        Assert.Same(spec, spa.Freq);
    }

    // ── Multi-segment union ───────────────────────────────────────────────────

    [Fact]
    public void TwoSegments_UnionIsSortedAndDeduped()
    {
        // Seg1: 1–2 GHz in 100 MHz steps → 11 pts
        // Seg2: 5–6 GHz in 50 MHz steps  → 21 pts
        // No overlap → union = 11 + 21 = 32 pts, sorted ascending.
        var seg1 = new FrequencySpec(1e9, 2e9, 100e6);
        var seg2 = new FrequencySpec(5e9, 6e9,  50e6);
        var spa  = new SParameterAnalysis("SP", [seg1, seg2]);

        var freqs = spa.Expand();
        Assert.Equal(32, freqs.Length);

        // Must be sorted ascending.
        for (int i = 1; i < freqs.Length; i++)
            Assert.True(freqs[i] > freqs[i - 1], $"Not sorted at index {i}");

        // Endpoints present.
        Assert.Equal(1e9, freqs[0],    1.0);
        Assert.Equal(2e9, freqs[10],   1.0);
        Assert.Equal(5e9, freqs[11],   1.0);
        Assert.Equal(6e9, freqs[^1],   1.0);
    }

    [Fact]
    public void TwoSegments_OverlappingPointsDeduped()
    {
        // Seg1: 1–3 GHz in 1 GHz steps → {1e9, 2e9, 3e9}
        // Seg2: 2–4 GHz in 1 GHz steps → {2e9, 3e9, 4e9}
        // Union (deduped): {1e9, 2e9, 3e9, 4e9}
        var seg1 = new FrequencySpec(1e9, 3e9, 1e9);
        var seg2 = new FrequencySpec(2e9, 4e9, 1e9);
        var spa  = new SParameterAnalysis("SP", [seg1, seg2]);

        var freqs = spa.Expand();
        Assert.Equal(4, freqs.Length);
        Assert.Equal(1e9, freqs[0], 1.0);
        Assert.Equal(2e9, freqs[1], 1.0);
        Assert.Equal(3e9, freqs[2], 1.0);
        Assert.Equal(4e9, freqs[3], 1.0);
    }

    [Fact]
    public void MultiSegment_GlobalsPassedThroughToEachSegment()
    {
        // start = "fstart" (global), stop = "fstop" (global) in seg1.
        // seg1: 1–5 GHz in 2 GHz steps → {1e9, 3e9, 5e9}
        // seg2: 10e9, 20e9, 30e9 (literal PointCount)
        var globals = new Dictionary<string, Value>
        {
            ["fstart"] = new Value(1e9),
            ["fstop"]  = new Value(5e9),
        };
        var seg1 = new FrequencySpec("fstart", "fstop", "2e9");
        var seg2 = new FrequencySpec("10e9", "30e9", 3);  // 3 pts: 10/20/30 GHz
        var spa  = new SParameterAnalysis("SP", [seg1, seg2]);

        var freqs = spa.Expand(globals);
        // {1e9, 3e9, 5e9} ∪ {10e9, 20e9, 30e9} = 6 pts
        Assert.Equal(6, freqs.Length);
        Assert.Equal(1e9,  freqs[0],  1.0);
        Assert.Equal(5e9,  freqs[2],  1.0);
        Assert.Equal(10e9, freqs[3],  1.0);
        Assert.Equal(30e9, freqs[^1], 1.0);
    }

    // ── MinSegments enforcement ───────────────────────────────────────────────

    [Fact]
    public void EmptySweepsList_Throws()
    {
        Assert.Throws<System.ArgumentException>(() =>
            new SParameterAnalysis("SP", (IReadOnlyList<FrequencySpec>)[]));
    }

    // ── CnlReader typed promotion ─────────────────────────────────────────────

    [Fact]
    public void CnlReader_SingleSweep_ProducesTypedSParameterAnalysis()
    {
        var src = "analysis SP type=sparam start=1 GHz stop=10 GHz step=1 GHz\n";
        var (_, tb) = new CnlReader().Read(src);

        Assert.Empty(tb.RawDirectives);
        var spa = Assert.Single(tb.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa);
        Assert.Equal("SP", spa.Name);
        Assert.Single(spa.Sweeps);
        Assert.Equal(FreqSpecMode.StepSize, spa.Sweeps[0].Mode);
        Assert.Equal(SweepKind.Linear,      spa.Sweeps[0].Kind);
    }

    [Fact]
    public void CnlReader_PointCountMode_ParsedCorrectly()
    {
        var src = "analysis SP type=sparam start=1 GHz stop=10 GHz npts=101\n";
        var (_, tb) = new CnlReader().Read(src);

        var spa = Assert.Single(tb.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa);
        Assert.Equal(FreqSpecMode.PointCount, spa.Sweeps[0].Mode);
        Assert.Equal(101, spa.Sweeps[0].NumPoints);

        var freqs = spa.Expand();
        Assert.Equal(101, freqs.Length);
        Assert.Equal(1e9,  freqs[0],  1e3);
        Assert.Equal(10e9, freqs[^1], 1e3);
    }

    [Fact]
    public void CnlReader_LogMode_ParsedCorrectly()
    {
        var src = "analysis SP type=sparam log start=1 GHz stop=10 GHz npts=11\n";
        var (_, tb) = new CnlReader().Read(src);

        var spa = Assert.Single(tb.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa);
        Assert.Equal(SweepKind.Log, spa.Sweeps[0].Kind);
    }

    [Fact]
    public void CnlReader_FusedUnitForm_NormalizedCorrectly()
    {
        // "start=1GHz" (fused, no space) must produce the same analysis as "start=1 GHz".
        var src = "analysis SP type=sparam start=1GHz stop=10GHz step=1GHz\n";
        var (_, tb) = new CnlReader().Read(src);

        var spa = Assert.Single(tb.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa);
        Assert.Equal(FreqSpecMode.StepSize, spa.Sweeps[0].Mode);

        var freqs = spa.Expand();
        Assert.Equal(10, freqs.Length);
        Assert.Equal(1e9,  freqs[0],  1.0);
        Assert.Equal(10e9, freqs[^1], 1.0);
    }

    // ── CnlWriter round-trip ──────────────────────────────────────────────────

    [Fact]
    public void CnlWriter_SingleSegment_RoundTrips()
    {
        var spec = new FrequencySpec("1e9", "10e9", "1e9");
        var spa  = new SParameterAnalysis("MySP", spec);
        var tb   = new TestBench("tb");
        tb.Analyses.Add(spa);

        var text = CnlWriter.Write(tb);
        var (_, tb2) = new CnlReader().Read(text);

        var spa2 = Assert.Single(tb2.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa2);
        Assert.Equal("MySP", spa2.Name);
        Assert.Single(spa2.Sweeps);
        Assert.Equal(FreqSpecMode.StepSize, spa2.Sweeps[0].Mode);

        // Expand must produce the same result.
        var freqs  = spa.Expand();
        var freqs2 = spa2.Expand();
        Assert.Equal(freqs.Length, freqs2.Length);
    }

    [Fact]
    public void CnlWriter_TwoSegments_RoundTrips()
    {
        var seg1 = new FrequencySpec("1e9", "2e9", 11);
        var seg2 = new FrequencySpec("5e9", "6e9", 21);
        var spa  = new SParameterAnalysis("SP", [seg1, seg2]);
        var tb   = new TestBench("tb");
        tb.Analyses.Add(spa);

        var text = CnlWriter.Write(tb);
        var (_, tb2) = new CnlReader().Read(text);

        // Two segments → CnlWriter emits two lines; CnlReader builds TWO typed analyses
        // (one per line, same name). Or it builds a single multi-segment analysis.
        // Current behavior: each line becomes a separate SParameterAnalysis.
        // The union of their Expand()s must equal spa.Expand().
        var allFreqs  = tb2.Analyses.OfType<SParameterAnalysis>()
                           .SelectMany(a => a.Expand())
                           .Distinct()
                           .OrderBy(f => f)
                           .ToArray();
        var expected  = spa.Expand();
        Assert.Equal(expected.Length, allFreqs.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], allFreqs[i], 1.0);
    }
}
