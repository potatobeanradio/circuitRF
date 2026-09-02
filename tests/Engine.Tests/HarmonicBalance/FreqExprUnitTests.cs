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

    // ── An unresolvable Tone is refused BY NAME, never defaulted ──────────────
    //
    // A Tone naming a variable the design does not have used to resolve to 1 GHz. Nothing
    // downstream could tell that from a tone the user typed, so the run got as far as the
    // commensurability check and then reported a grid nobody asked for (f0=1E+09 Hz) while
    // blaming the source for sitting off it. The analysis card, which does not substitute, was
    // already saying "unknown" — these tests hold the two answers together.

    [Fact]
    public void Hb_Resolve_UnknownToneVariable_IsRefusedByName()
    {
        var globals = new Dictionary<string, Value> { ["RFfreq1"] = new Value(1.805e9) };
        var hba = new HarmonicBalanceAnalysis("HB3")
        {
            ToneExpr = "RFfreq",     // the design has RFfreq1, not RFfreq
            ToneUnit = "GHz",
            MaxHarmonicExpr = "5",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => HbEngine.Resolve(hba, globals, globalsWithUnit: null));

        Assert.Contains("RFfreq", ex.Message);              // the name that could not be resolved
        Assert.Contains("Tone", ex.Message);                // the field it was typed into
        Assert.Contains("HB3", ex.Message);                 // the analysis
        Assert.Contains("undefined", ex.Message);
        // The whole point: no substituted grid is described, and no source is blamed.
        Assert.DoesNotContain("1E+09", ex.Message);
        Assert.DoesNotContain("Commensurability", ex.Message);
    }

    [Fact]
    public void Hb_Resolve_UnknownToneVariable_MultiTone_NamesTheToneIndex()
    {
        var globals = new Dictionary<string, Value> { ["RFfreq"] = new Value(2.0) };
        var hba = new HarmonicBalanceAnalysis("HB2")
        {
            NumFreqsExpr = "2",
            ToneExprs = ["RFfreq", "LOfreq"],
            ToneUnits = ["GHz", "GHz"],
            MaxMixOrderExpr = "5",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => HbEngine.Resolve(hba, globals, globalsWithUnit: null));

        Assert.Contains("Tone[2]", ex.Message);
        Assert.Contains("LOfreq", ex.Message);
    }

    [Fact]
    public void Hb_Resolve_UnparseableTone_IsRefused()
    {
        var hba = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2 GHz", ToneUnit = "GHz" };

        // "2 GHz" is a parse error in an expression field (the unit is a field of its own) —
        // it must refuse rather than silently become 1 GHz.
        var ex = Assert.Throws<InvalidOperationException>(
            () => HbEngine.Resolve(hba, new Dictionary<string, Value>(), null));
        Assert.Contains("Tone", ex.Message);
    }

    [Fact]
    public void Loadpull_Resolve_UnknownToneVariable_IsRefusedByName()
    {
        var lpa = new LoadpullAnalysis("LP1") { ToneExpr = "RFfreq", ToneUnit = "GHz" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => CircuitRF.Engine.Loadpull.LoadpullEngine.Resolve(
                      lpa, new Dictionary<string, Value>(), globalsWithUnit: null));

        Assert.Contains("RFfreq", ex.Message);
        Assert.Contains("Tone", ex.Message);
        Assert.Contains("LP1", ex.Message);
    }

    // T8 (pure predicate) lives in Core.Tests/Expressions/FreqUnitTests.cs:
    //   FreqUnit_LooksLikeUnitMismatch_PowersOf1000
    //   FreqUnit_LooksLikeUnitMismatch_NoMatch
    // An integration test via a degenerate netlist is not viable: HbEngine.Run requires at least
    // one nonlinear device to build a non-empty interface; testing the hint string through the
    // engine would demand a full SDD circuit, adding overhead with no coverage gain over the
    // pure-function test of FreqUnit.LooksLikeUnitMismatch.
}
