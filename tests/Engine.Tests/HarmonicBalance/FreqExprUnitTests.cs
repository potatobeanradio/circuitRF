using System;
using System.Collections.Generic;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate tests for HB frequency expression + unit handling (brief-analysis-freq-expr-unit.md, Tests 7-8).
/// </summary>
public class FreqExprUnitTests
{
    private static (Library, TestBench) ParseCnl(string cnl)
        => new CnlReader().Read(cnl, testBenchName: "tb");

    // ── T7: HbEngine.Resolve applies field unit when var has no declared unit ──

    [Fact]
    public void Hb_Resolve_VarToneGHz()
    {
        // VAR RFfreq = 2.4, ToneUnit = GHz → f0 should be 2.4e9 Hz.
        var globals = new Dictionary<string, Value>
        {
            ["RFfreq"] = new Value(2.4),
        };
        // Empty globalsWithUnit → var-unit-wins not triggered → multiply by GHz.
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "RFfreq",
            ToneUnit = "GHz",
            MaxHarmonicExpr = "7",
        };
        var p = HbEngine.Resolve(hba, globals, globalsWithUnit: null);
        Assert.Equal(2.4e9, p.ToneHz, precision: 3);
    }

    [Fact]
    public void Hb_Resolve_VarWithUnitWins()
    {
        // VAR RFfreq declared with unit → already in Hz; GHz field unit must be ignored.
        var globals = new Dictionary<string, Value>
        {
            ["RFfreq"] = new Value(2.4e9),
        };
        var globalsWithUnit = new HashSet<string>(StringComparer.Ordinal) { "RFfreq" };
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "RFfreq",
            ToneUnit = "GHz",
            MaxHarmonicExpr = "7",
        };
        var p = HbEngine.Resolve(hba, globals, globalsWithUnit);
        Assert.Equal(2.4e9, p.ToneHz, precision: 3);
    }

    [Fact]
    public void Hb_Resolve_NumericTone_BackCompat()
    {
        // Old-style baked numeric tone (ToneUnit="Hz", ToneExpr="2.4e9") still resolves correctly.
        var globals = new Dictionary<string, Value>();
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "2.4e9",
            ToneUnit = "Hz",
            MaxHarmonicExpr = "7",
        };
        var p = HbEngine.Resolve(hba, globals, null);
        Assert.Equal(2.4e9, p.ToneHz, precision: 3);
    }

    // T8 (pure predicate) lives in Core.Tests/Expressions/FreqUnitTests.cs:
    //   FreqUnit_LooksLikeUnitMismatch_PowersOf1000
    //   FreqUnit_LooksLikeUnitMismatch_NoMatch
    // An integration test via a degenerate netlist is not viable: HbEngine.Run requires at least
    // one nonlinear device to build a non-empty interface; testing the hint string through the
    // engine would demand a full SDD circuit, adding overhead with no coverage gain over the
    // pure-function test of FreqUnit.LooksLikeUnitMismatch.
}
