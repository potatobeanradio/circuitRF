using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Loadpull UI brief 07 gate: CnlWriter → text → CnlReader round-trips LoadpullAnalysis and
/// LoadpullPursuitAnalysis losslessly (the .cnl path used by Run), and the Grid/OutputGrid relative
/// paths resolve against the netlist's own directory (the engine's resolution base — where Run writes
/// netlist.cnl). The latter is the one real plumbing piece this brief locks down.
/// </summary>
public class LoadpullCnlRoundTripTests
{
    // ── LP: every field round-trips ──────────────────────────────────────────

    [Fact]
    public void Loadpull_Directive_RoundTrips_AllFields()
    {
        var lp = new LoadpullAnalysis("LP1")
        {
            Enabled = false,
            ToneExpr = "RFfreq", ToneUnit = "GHz",
            MaxHarmonicExpr = "7",
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            SweepExpr = "Source", TuneHarmExpr = "2",
            GridPath = "grids/hero3.gam",
            CompressionExpr = "1", GainTypeExpr = "Gp",
            PinStartExpr = "-15", PinStepExpr = "0.5", PinMaxExpr = "25",
            TickleExpr = "off", MaxIterExpr = "50",
            FFTOverSampleExpr = "2", TolExpr = "1e-8",
            DriveSteppingExpr = "Always", GuardHarmonicExpr = "3",
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(lp);

        var text = CnlWriter.Write(tb);
        var (_, tb2) = new CnlReader().Read(text);   // no sourceDir → relative Grid stays relative

        var r = tb2.Analyses.OfType<LoadpullAnalysis>().Single();
        Assert.Equal("LP1", r.Name);
        Assert.False(r.Enabled);
        Assert.Equal("RFfreq", r.ToneExpr);
        Assert.Equal("GHz",    r.ToneUnit);
        Assert.Equal("7",   r.MaxHarmonicExpr);
        Assert.Equal("LoadTuner1",   r.LoadTunerName);
        Assert.Equal("SourceTuner1", r.SourceTunerName);
        Assert.Equal("Source", r.SweepExpr);
        Assert.Equal("2",   r.TuneHarmExpr);
        Assert.Equal("grids/hero3.gam", r.GridPath);
        Assert.Equal("1",   r.CompressionExpr);
        Assert.Equal("Gp",  r.GainTypeExpr);
        Assert.Equal("-15", r.PinStartExpr);
        Assert.Equal("0.5", r.PinStepExpr);
        Assert.Equal("25",  r.PinMaxExpr);
        Assert.Equal("off", r.TickleExpr);
        Assert.Equal("50",  r.MaxIterExpr);
        Assert.Equal("2",   r.FFTOverSampleExpr);
        Assert.Equal("1e-8", r.TolExpr);
        Assert.Equal("Always", r.DriveSteppingExpr);
        Assert.Equal("3",   r.GuardHarmonicExpr);
    }

    // ── Freq-swept LP: the parametric-sweep chain over the tone VAR round-trips ──
    // (FreqSweptLoadpull brief — this is the .cnl path Run uses to reach the engine.)

    [Fact]
    public void FreqSweptLoadpull_Chain_RoundTrips()
    {
        var lp = new LoadpullAnalysis("LP1")
        {
            ToneExpr = "RFfreq", ToneUnit = "GHz",
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            GridPath = "grids/hero3.gam",
        };
        var psa = new ParametricSweepAnalysis("LP1_sweep_RFfreq", "RFfreq",
            new[] { 1.8e9, 2.0e9, 2.2e9 }, "LP1");
        var tb = new TestBench("tb");
        tb.Analyses.Add(lp);
        tb.Analyses.Add(psa);

        var (_, tb2) = new CnlReader().Read(CnlWriter.Write(tb));

        var rlp  = tb2.Analyses.OfType<LoadpullAnalysis>().Single();
        var rpsa = tb2.Analyses.OfType<ParametricSweepAnalysis>().Single();
        Assert.Equal("RFfreq", rlp.ToneExpr);
        Assert.Equal("LP1",    rpsa.InnerAnalysisName);
        Assert.Equal("RFfreq", rpsa.SweepVarName);
        Assert.Equal(new[] { 1.8e9, 2.0e9, 2.2e9 }, rpsa.SweepValues);
    }

    // ── LPP: every field round-trips (incl. pursuit keys) ────────────────────

    [Fact]
    public void LoadpullPursuit_Directive_RoundTrips_AllFields()
    {
        var lpp = new LoadpullPursuitAnalysis("LPP1")
        {
            ToneExpr = "2", ToneUnit = "GHz",
            LoadTunerName = "LoadTuner1", SourceTunerName = "SourceTuner1",
            PinMaxExpr = "30",
            EffTypeExpr = "PAE", ZsourceOBOExpr = "6", SearchMethodExpr = "IteratedQuadratic",
            OutputGridPath = "grids/out.gam",
            Vswr1Expr = "1.2", Vswr1ResolutionExpr = "5",
            Vswr2Expr = "2.5", Vswr2ResolutionExpr = "6",
            KeepNonconvergingExpr = "true", NonconvergentVswrExpr = "1.1",
            CreateLoadpullResultExpr = "false", LoadpullResultZsourceExpr = "MXP",
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(lpp);

        var text = CnlWriter.Write(tb);
        var (_, tb2) = new CnlReader().Read(text);

        var r = tb2.Analyses.OfType<LoadpullPursuitAnalysis>().Single();
        Assert.Equal("LPP1", r.Name);
        Assert.Equal("2",   r.ToneExpr);
        Assert.Equal("GHz", r.ToneUnit);
        Assert.Equal("30",  r.PinMaxExpr);
        Assert.Equal("PAE", r.EffTypeExpr);
        Assert.Equal("6",   r.ZsourceOBOExpr);
        Assert.Equal("IteratedQuadratic", r.SearchMethodExpr);
        Assert.Equal("grids/out.gam", r.OutputGridPath);
        Assert.Equal("1.2", r.Vswr1Expr);
        Assert.Equal("5",   r.Vswr1ResolutionExpr);
        Assert.Equal("2.5", r.Vswr2Expr);
        Assert.Equal("6",   r.Vswr2ResolutionExpr);
        Assert.Equal("true", r.KeepNonconvergingExpr);
        Assert.Equal("1.1", r.NonconvergentVswrExpr);
        Assert.Equal("false", r.CreateLoadpullResultExpr);
        Assert.Equal("MXP", r.LoadpullResultZsourceExpr);
    }

    // ── LPP with no OutputGrid round-trips as null ───────────────────────────

    [Fact]
    public void LoadpullPursuit_NoOutputGrid_RoundTripsNull()
    {
        var lpp = new LoadpullPursuitAnalysis("LPP1")
        {
            LoadTunerName = "L", SourceTunerName = "S", OutputGridPath = null,
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(lpp);

        var (_, tb2) = new CnlReader().Read(CnlWriter.Write(tb));
        Assert.Null(tb2.Analyses.OfType<LoadpullPursuitAnalysis>().Single().OutputGridPath);
    }

    // ── Grid path resolves against the netlist's directory (Run's base) ──────

    [Fact]
    public void Loadpull_RelativeGrid_ResolvesAgainstNetlistDir()
    {
        var netlistDir = Path.Combine(Path.GetTempPath(), "crf_run_dir");
        var lp = new LoadpullAnalysis("LP1")
        {
            LoadTunerName = "L", SourceTunerName = "S", GridPath = "results/hero3.gam",
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(lp);

        var text = CnlWriter.Write(tb);
        // Simulate Run: CnlReader.ReadFile sets sourceDirectory to the netlist.cnl directory.
        var (_, tb2) = new CnlReader().Read(text, sourceDirectory: netlistDir);

        var r = tb2.Analyses.OfType<LoadpullAnalysis>().Single();
        Assert.Equal(Path.GetFullPath(Path.Combine(netlistDir, "results/hero3.gam")), r.GridPath);
        Assert.True(Path.IsPathRooted(r.GridPath));
    }

    [Fact]
    public void Pursuit_RelativeOutputGrid_ResolvesAgainstNetlistDir()
    {
        var netlistDir = Path.Combine(Path.GetTempPath(), "crf_run_dir");
        var lpp = new LoadpullPursuitAnalysis("LPP1")
        {
            LoadTunerName = "L", SourceTunerName = "S", OutputGridPath = "results/out.gam",
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(lpp);

        var (_, tb2) = new CnlReader().Read(CnlWriter.Write(tb), sourceDirectory: netlistDir);

        var r = tb2.Analyses.OfType<LoadpullPursuitAnalysis>().Single();
        Assert.Equal(Path.GetFullPath(Path.Combine(netlistDir, "results/out.gam")), r.OutputGridPath);
    }

    // ── Absolute Grid path is preserved (not re-based) ───────────────────────

    [Fact]
    public void Loadpull_AbsoluteGrid_PreservedVerbatim()
    {
        var abs = Path.Combine(Path.GetTempPath(), "elsewhere", "x.gam");
        var lp = new LoadpullAnalysis("LP1")
        {
            LoadTunerName = "L", SourceTunerName = "S", GridPath = abs,
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(lp);

        var (_, tb2) = new CnlReader().Read(CnlWriter.Write(tb),
            sourceDirectory: Path.Combine(Path.GetTempPath(), "crf_run_dir"));

        Assert.Equal(abs, tb2.Analyses.OfType<LoadpullAnalysis>().Single().GridPath);
    }
}
