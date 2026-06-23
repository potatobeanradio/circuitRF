using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Loadpull UI brief 04b gate: the loadpull AND loadpull-pursuit tone resolve to Hz with the exact
/// same var-unit-wins rule HB uses (FreqUnit.ResolveHz). A VAR with or without a unit works as the
/// tone with no glitches; Hz literals stay byte-compatible. Mirrors FreqExprUnitTests (HB).
/// </summary>
public class LoadpullToneUnitTests
{
    // Locate an existing .gam grid file (LoadpullEngine.Resolve requires a real Grid path).
    private static string GridPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero3", "hero3_load.gam");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("testdata/Hero3/hero3_load.gam not found");
    }

    private static LoadpullAnalysis Lpa(string toneExpr, string toneUnit) =>
        new("LP1")
        {
            ToneExpr = toneExpr,
            ToneUnit = toneUnit,
            LoadTunerName = "Load", SourceTunerName = "Src",
            GridPath = GridPath(),
        };

    private static LoadpullPursuitAnalysis Lpp(string toneExpr, string toneUnit) =>
        new("LPP1")
        {
            ToneExpr = toneExpr,
            ToneUnit = toneUnit,
            LoadTunerName = "Load", SourceTunerName = "Src",
        };

    // ── LoadpullEngine.Resolve ───────────────────────────────────────────────

    [Fact]
    public void Lp_UnitlessVar_FieldGHz_AppliesGHz()   // the glitch case
    {
        var globals = new Dictionary<string, Value> { ["RFfreq"] = new Value(2.0) };
        var p = LoadpullEngine.Resolve(Lpa("RFfreq", "GHz"), globals, globalsWithUnit: null);
        Assert.Equal(2e9, p.ToneHz, precision: 3);
    }

    [Fact]
    public void Lp_UnitdVar_VarWins_NoDoubleScale()
    {
        var globals = new Dictionary<string, Value> { ["RFfreq"] = new Value(2e9) };
        var gwu     = new HashSet<string>(StringComparer.Ordinal) { "RFfreq" };
        var p = LoadpullEngine.Resolve(Lpa("RFfreq", "GHz"), globals, gwu);
        Assert.Equal(2e9, p.ToneHz, precision: 3);   // not 2e18
    }

    [Fact]
    public void Lp_Literal_FieldGHz()
    {
        var p = LoadpullEngine.Resolve(Lpa("2", "GHz"), new Dictionary<string, Value>(), null);
        Assert.Equal(2e9, p.ToneHz, precision: 3);
    }

    [Fact]
    public void Lp_HzLiteral_BackCompat()
    {
        var p = LoadpullEngine.Resolve(Lpa("2e9", "Hz"), new Dictionary<string, Value>(), null);
        Assert.Equal(2e9, p.ToneHz, precision: 3);
    }

    // ── LoadpullPursuitEngine.Resolve ────────────────────────────────────────

    [Fact]
    public void Lpp_UnitlessVar_FieldGHz_AppliesGHz()
    {
        var globals = new Dictionary<string, Value> { ["RFfreq"] = new Value(2.0) };
        var pp = LoadpullPursuitEngine.Resolve(Lpp("RFfreq", "GHz"), globals, globalsWithUnit: null);
        Assert.Equal(2e9, pp.LpParams.ToneHz, precision: 3);
    }

    [Fact]
    public void Lpp_UnitdVar_VarWins_NoDoubleScale()
    {
        var globals = new Dictionary<string, Value> { ["RFfreq"] = new Value(2e9) };
        var gwu     = new HashSet<string>(StringComparer.Ordinal) { "RFfreq" };
        var pp = LoadpullPursuitEngine.Resolve(Lpp("RFfreq", "GHz"), globals, gwu);
        Assert.Equal(2e9, pp.LpParams.ToneHz, precision: 3);
    }

    [Fact]
    public void Lpp_Literal_FieldGHz()
    {
        var pp = LoadpullPursuitEngine.Resolve(Lpp("2", "GHz"), new Dictionary<string, Value>(), null);
        Assert.Equal(2e9, pp.LpParams.ToneHz, precision: 3);
    }

    [Fact]
    public void Lpp_HzLiteral_BackCompat()
    {
        var pp = LoadpullPursuitEngine.Resolve(Lpp("2e9", "Hz"), new Dictionary<string, Value>(), null);
        Assert.Equal(2e9, pp.LpParams.ToneHz, precision: 3);
    }

    // ── Reader / writer round-trip (mirror HB: Tone= + ToneUnit= keys) ────────

    private static TestBench RoundTrip(Analysis a)
    {
        var tb = new TestBench("tb");
        tb.Analyses.Add(a);
        var cnl = CnlWriter.Write(tb);
        var (_, tb2) = new CnlReader().Read(cnl);
        return tb2;
    }

    [Fact]
    public void Lp_RoundTrip_VarTone_PreservesExprAndUnit()
    {
        var tb2 = RoundTrip(Lpa("RFfreq", "GHz"));
        var a = tb2.Analyses.OfType<LoadpullAnalysis>().Single();
        Assert.Equal("RFfreq", a.ToneExpr);
        Assert.Equal("GHz", a.ToneUnit);
    }

    [Fact]
    public void Lp_RoundTrip_HzLiteral_DefaultsHz()
    {
        var a0 = new LoadpullAnalysis("LP1")
        {
            ToneExpr = "2e9", ToneUnit = "Hz",
            LoadTunerName = "Load", SourceTunerName = "Src", GridPath = GridPath(),
        };
        var a = RoundTrip(a0).Analyses.OfType<LoadpullAnalysis>().Single();
        Assert.Equal("2e9", a.ToneExpr);
        Assert.Equal("Hz", a.ToneUnit);
    }

    [Fact]
    public void Lpp_RoundTrip_VarTone_PreservesExprAndUnit()
    {
        var a = RoundTrip(Lpp("2", "GHz")).Analyses.OfType<LoadpullPursuitAnalysis>().Single();
        Assert.Equal("2", a.ToneExpr);
        Assert.Equal("GHz", a.ToneUnit);
    }
}
