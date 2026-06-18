using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Gate tests for the <c>enabled=false</c> round-trip in .cnl
/// (brief-sweep-revamp-1-persistence Part A).
/// Verifies that CnlWriter emits the token and CnlReader restores it for all
/// analysis types, and that omitting the token defaults to Enabled = true.
/// </summary>
public sealed class CnlEnabledTests
{
    // ── Helper: write a TestBench to .cnl and read it back ───────────────────

    private static TestBench RoundTrip(TestBench tb)
    {
        string cnl = CnlWriter.Write(tb);
        var (_, tb2) = new CnlReader().Read(cnl);
        return tb2;
    }

    // ── Default: absent token → Enabled = true ───────────────────────────────

    [Fact]
    public void DcAnalysis_NoEnabledToken_DefaultsToTrue()
    {
        const string cnl = "analysis DC1 type=dc";
        var (_, tb) = new CnlReader().Read(cnl);
        var dc = Assert.IsType<DcAnalysis>(Assert.Single(tb.Analyses));
        Assert.True(dc.Enabled);
    }

    // ── DC disabled ───────────────────────────────────────────────────────────

    [Fact]
    public void DcAnalysis_Disabled_RoundTrips()
    {
        var tb = new TestBench("tb");
        tb.Analyses.Add(new DcAnalysis("DC1") { Enabled = false });

        var tb2 = RoundTrip(tb);
        var dc = Assert.IsType<DcAnalysis>(Assert.Single(tb2.Analyses));
        Assert.Equal("DC1", dc.Name);
        Assert.False(dc.Enabled);
    }

    // ── Spec sweep disabled (StepSize) ────────────────────────────────────────

    [Fact]
    public void SpecSweep_StepSize_Disabled_SpecAndEnabledPreserved()
    {
        var tb = new TestBench("tb");
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1"));
        var spec = new SweepSpec(-30.0, 0.0, 0.5, SweepAxisMode.StepSize, SweepKind.Linear);
        tb.Analyses.Add(new ParametricSweepAnalysis("SW1", "Pin", spec, "HB1") { Enabled = false });

        var tb2 = RoundTrip(tb);
        var sw = tb2.Analyses.OfType<ParametricSweepAnalysis>().Single(a => a.Name == "SW1");

        Assert.False(sw.Enabled);
        Assert.NotNull(sw.Spec);
        Assert.Equal(SweepAxisMode.StepSize, sw.Spec!.Mode);
        Assert.Equal(-30.0, sw.Spec.Start,       precision: 9);
        Assert.Equal(  0.0, sw.Spec.Stop,        precision: 9);
        Assert.Equal(  0.5, sw.Spec.StepOrCount, precision: 9);
        Assert.Equal(SweepKind.Linear, sw.Spec.Kind);
    }

    // ── Spec sweep disabled (PointCount) ─────────────────────────────────────

    [Fact]
    public void SpecSweep_PointCount_Disabled_SpecAndEnabledPreserved()
    {
        var tb = new TestBench("tb");
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1"));
        var spec = new SweepSpec(1.0, 5.0, 11, SweepAxisMode.PointCount, SweepKind.Linear);
        tb.Analyses.Add(new ParametricSweepAnalysis("SW2", "Vgs", spec, "HB1") { Enabled = false });

        var tb2 = RoundTrip(tb);
        var sw = tb2.Analyses.OfType<ParametricSweepAnalysis>().Single(a => a.Name == "SW2");

        Assert.False(sw.Enabled);
        Assert.NotNull(sw.Spec);
        Assert.Equal(SweepAxisMode.PointCount, sw.Spec!.Mode);
        Assert.Equal( 1.0, sw.Spec.Start,       precision: 9);
        Assert.Equal( 5.0, sw.Spec.Stop,        precision: 9);
        Assert.Equal(11.0, sw.Spec.StepOrCount, precision: 9);
    }

    // ── Mixed: enabled + disabled analyses all survive ────────────────────────

    [Fact]
    public void Mixed_EnabledAndDisabled_AllPreserved()
    {
        var tb = new TestBench("tb");
        tb.Analyses.Add(new DcAnalysis("DC1"));                          // enabled (default)
        tb.Analyses.Add(new DcAnalysis("DC2") { Enabled = false });
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1"));             // enabled (default)
        var spec = new SweepSpec(1.0, 3.0, 5, SweepAxisMode.PointCount, SweepKind.Linear);
        tb.Analyses.Add(new ParametricSweepAnalysis("SW1", "x", spec, "HB1") { Enabled = false });

        var tb2 = RoundTrip(tb);
        Assert.Equal(4, tb2.Analyses.Count);

        Assert.True (tb2.Analyses[0].Enabled, "DC1 should be enabled");
        Assert.False(tb2.Analyses[1].Enabled, "DC2 should be disabled");
        Assert.True (tb2.Analyses[2].Enabled, "HB1 should be enabled");
        Assert.False(tb2.Analyses[3].Enabled, "SW1 should be disabled");
    }
}
