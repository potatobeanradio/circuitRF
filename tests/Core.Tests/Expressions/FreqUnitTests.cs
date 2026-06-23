using System.Collections.Generic;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Gate tests for FreqUnit (brief-analysis-freq-expr-unit.md, Tests 1-6).
/// </summary>
public class FreqUnitTests
{
    private static (Library, TestBench) ParseCnl(string cnl)
        => new CnlReader().Read(cnl, testBenchName: "tb");

    // ── T1: numeric coefficient × field unit ─────────────────────────────────

    [Fact]
    public void FreqUnit_ResolveHz_NumericTimesUnit()
    {
        var globals = new Dictionary<string, Value>();

        Assert.Equal(2.4e9, FreqUnit.ResolveHz("2.4", "GHz", globals, null), precision: 3);
        Assert.Equal(2.0,   FreqUnit.ResolveHz("2",   "Hz",  globals, null), precision: 10);
        Assert.Equal(500e6, FreqUnit.ResolveHz("500", "MHz", globals, null), precision: 3);
        Assert.Equal(1e3,   FreqUnit.ResolveHz("1",   "kHz", globals, null), precision: 10);
    }

    // ── T2: var-unit-wins ────────────────────────────────────────────────────

    [Fact]
    public void FreqUnit_VarUnitWins()
    {
        // VAR RFfreq declared WITHOUT unit — field unit applies.
        var globals = new Dictionary<string, Value>
        {
            ["RFfreq"] = new Value(2.0),
        };
        Assert.Equal(2e9, FreqUnit.ResolveHz("RFfreq", "GHz", globals, globalsWithUnit: null), precision: 3);
        Assert.Equal(2e9, FreqUnit.ResolveHz("RFfreq", "GHz", globals, globalsWithUnit: []), precision: 3);

        // VAR RFfreq declared WITH unit — var-unit-wins, field GHz ignored, value returned as-is.
        var globalsWithUnit = new HashSet<string>(System.StringComparer.Ordinal) { "RFfreq" };
        var globalsHz = new Dictionary<string, Value>
        {
            ["RFfreq"] = new Value(2e9),   // already in Hz because unit was on the VAR
        };
        Assert.Equal(2e9, FreqUnit.ResolveHz("RFfreq", "GHz", globalsHz, globalsWithUnit), precision: 3);

        // Pure numeric literal: no refs → field unit always applies, even when globalsWithUnit is set.
        Assert.Equal(2e9, FreqUnit.ResolveHz("2", "GHz", globals, globalsWithUnit), precision: 3);
    }

    // ── T3: HB CNL round-trip with symbolic tone ─────────────────────────────

    [Fact]
    public void Hb_Cnl_RoundTrip_SymbolicTone()
    {
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "RFfreq",
            ToneUnit = "GHz",
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(hb);

        string cnl = CnlWriter.Write(tb);

        // CnlWriter must quote the expression and emit ToneUnit.
        Assert.Contains("Tone=\"RFfreq\"", cnl);
        Assert.Contains("ToneUnit=GHz",    cnl);
        Assert.DoesNotContain("* 1000000000", cnl);

        // CnlReader must parse it back without mangling.
        var (_, tbBack) = ParseCnl(cnl);
        var hbBack = Assert.Single(tbBack.Analyses.OfType<HarmonicBalanceAnalysis>());
        Assert.Equal("RFfreq", hbBack.ToneExpr);
        Assert.Equal("GHz",    hbBack.ToneUnit);
    }

    [Fact]
    public void Hb_Cnl_RoundTrip_ExprWithWhitespace()
    {
        // Expression with whitespace (e.g. "2 * f0") must survive quoting round-trip.
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2 * f0",
            ToneUnit = "GHz",
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(hb);

        string cnl = CnlWriter.Write(tb);
        Assert.Contains("Tone=\"2 * f0\"", cnl);

        var (_, tbBack) = ParseCnl(cnl);
        var hbBack = Assert.Single(tbBack.Analyses.OfType<HarmonicBalanceAnalysis>());
        Assert.Equal("2 * f0", hbBack.ToneExpr);
        Assert.Equal("GHz",    hbBack.ToneUnit);
    }

    // ── T4: S-param CNL round-trip with symbolic start ───────────────────────

    [Fact]
    public void Sparam_Cnl_RoundTrip_SymbolicStart()
    {
        var freq = new FrequencySpec("f_low", "f_high", 101, SweepKind.Linear, "GHz", "GHz");
        var spa  = new SParameterAnalysis("SP1", freq);
        var tb   = new TestBench("tb");
        tb.Analyses.Add(spa);

        string cnl = CnlWriter.Write(tb);

        Assert.Contains("start=\"f_low\"",  cnl);
        Assert.Contains("startUnit=GHz",    cnl);
        Assert.Contains("stop=\"f_high\"",  cnl);
        Assert.Contains("stopUnit=GHz",     cnl);

        var (_, tbBack) = ParseCnl(cnl);
        var spaBack = Assert.Single(tbBack.Analyses.OfType<SParameterAnalysis>());
        var segBack = Assert.Single(spaBack.Sweeps);
        Assert.Equal("f_low",  segBack.StartExpr);
        Assert.Equal("GHz",    segBack.StartUnit);
        Assert.Equal("f_high", segBack.StopExpr);
        Assert.Equal("GHz",    segBack.StopUnit);
    }

    // ── T5: back-compat — old numeric tone with no ToneUnit key ─────────────

    [Fact]
    public void Cnl_BackCompat_NumericTone()
    {
        // An old-style CNL file with no ToneUnit key and baked Hz value.
        const string cnl = """
            analysis HB1 type=hb Tone=2.4e9 MaxHarm=7 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0 Lambda=1 MaxIter=100
            """;
        var (_, tb) = ParseCnl(cnl);
        var hb = Assert.Single(tb.Analyses.OfType<HarmonicBalanceAnalysis>());
        Assert.Equal("2.4e9", hb.ToneExpr);
        Assert.Equal("Hz",    hb.ToneUnit);   // default when absent
    }

    [Fact]
    public void Cnl_BackCompat_NumericSparam()
    {
        // Old-style CNL with baked Hz values, no unit keys.
        const string cnl = """
            analysis SP1 type=sparam start=1e9 stop=10e9 npts=101
            """;
        var (_, tb) = ParseCnl(cnl);
        var spa = Assert.Single(tb.Analyses.OfType<SParameterAnalysis>());
        Assert.Equal("Hz", spa.Sweeps[0].StartUnit);
        Assert.Equal("Hz", spa.Sweeps[0].StopUnit);
    }

    // ── T6: FrequencySpec.Expand applies unit ────────────────────────────────

    [Fact]
    public void FrequencySpec_Expand_AppliesUnit()
    {
        // 5-point PointCount sweep 1–5 GHz should expand to [1e9, 2e9, 3e9, 4e9, 5e9].
        var seg = new FrequencySpec("1", "5", 5, SweepKind.Linear, "GHz", "GHz");
        var pts = seg.Expand();
        Assert.Equal(5, pts.Length);
        Assert.Equal(1e9, pts[0], precision: 3);
        Assert.Equal(5e9, pts[4], precision: 3);
    }

    [Fact]
    public void FrequencySpec_Expand_VarUnitWins()
    {
        // Variable with unit → already in Hz; field "GHz" ignored.
        var globals = new Dictionary<string, Value>
        {
            ["fstart"] = new Value(1e9),
            ["fstop"]  = new Value(5e9),
        };
        var gwu = new HashSet<string>(System.StringComparer.Ordinal) { "fstart", "fstop" };

        var seg = new FrequencySpec("fstart", "fstop", 5, SweepKind.Linear, "GHz", "GHz");
        var pts = seg.Expand(globals, gwu);
        Assert.Equal(5, pts.Length);
        Assert.Equal(1e9, pts[0], precision: 3);
        Assert.Equal(5e9, pts[4], precision: 3);
    }

    // ── T8: FreqUnit.LooksLikeUnitMismatch — pure predicate for commensurability hint ─

    [Fact]
    public void FreqUnit_LooksLikeUnitMismatch_PowersOf1000()
    {
        // Exact scale factors.
        Assert.Equal(3, FreqUnit.LooksLikeUnitMismatch(2.0,   2e9));   // Hz vs GHz → 1000³
        Assert.Equal(2, FreqUnit.LooksLikeUnitMismatch(500.0, 500e6)); // Hz vs MHz → 1000²
        Assert.Equal(1, FreqUnit.LooksLikeUnitMismatch(1.0,   1e3));   // Hz vs kHz → 1000¹
        // Symmetric (b/a, not a/b).
        Assert.Equal(3, FreqUnit.LooksLikeUnitMismatch(2e9,   2.0));
    }

    [Fact]
    public void FreqUnit_LooksLikeUnitMismatch_NoMatch()
    {
        Assert.Equal(0, FreqUnit.LooksLikeUnitMismatch(2.0, 2.1));    // tiny ratio — fine
        Assert.Equal(0, FreqUnit.LooksLikeUnitMismatch(2.0, 100.0));  // 50× — not a power of 1000
        Assert.Equal(0, FreqUnit.LooksLikeUnitMismatch(2.0, 2e4));    // 10000× — not a multiple of 3 in log10
        Assert.Equal(0, FreqUnit.LooksLikeUnitMismatch(0.0, 2e9));    // zero → 0
        Assert.Equal(0, FreqUnit.LooksLikeUnitMismatch(2.0, 0.0));    // zero → 0
        // Near-power-of-1000 but outside 0.1% tolerance.
        Assert.Equal(0, FreqUnit.LooksLikeUnitMismatch(1.0, 1.005e9));
    }
}
