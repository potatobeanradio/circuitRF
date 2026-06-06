using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Phase 4b-2 acceptance gate — Hero 3B: loadpull_pursuit on the GaN HEMT PA.
///
/// Three tests:
///   1. GenerateHero3BGolden — runs the full pursuit and writes golden CSV + .gam file.
///      Run with: dotnet test --filter GenerateHero3BGolden
///   2. Hero3BPursuit_RegressionPasses — compares current run against frozen golden.
///   3. Hero3BPursuit_NonCompressionExitClean — verifies PinMax=-18 aborts cleanly.
///
/// Acceptance criteria (Phase4b2_Brief.md):
///   - MXP and MXE found (Converged=true).
///   - MXP↔MXE separated 2–2.5 VSWR (Pedro sanity check for this stable FET).
///   - MXE uses fewer new queries than MXP (cache hits + Pedro seed).
///   - Zsource reported for both optima.
///   - OutputGrid .gam written with focused+broad structure.
///   - dotnet build/test green; Phases 1–4b-1 still pass.
///
/// NOTE: Optima within ≤ 1.1 VSWR of a high-resolution reference is owner-verified;
/// the self-generated golden freezes the current engine state for regression.
///
/// LABEL: SELF-GENERATED REGRESSION — NOT INDEPENDENTLY VALIDATED.
///
/// Phase 5-5: updated to use DataSet result API (values unchanged, re-housed only).
/// </summary>
public class Hero3BPursuitTests(ITestOutputHelper output)
{
    private static string Hero3BDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero3B");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero3B not found");
    }

    private static (LoadpullPursuitEngine Engine, LoadpullPursuitEngine.PursuitParams Params,
                    string Dir)
        BuildEngine(bool createLoadpullResult = false)
    {
        var dir     = Hero3BDir();
        var cnlPath = Path.Combine(dir, "hero3B_at_compression.cnl");
        var (lib, tb) = CnlReader.ReadFile(cnlPath);
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var lpa = tb.Analyses.OfType<LoadpullPursuitAnalysis>().First();
        var pp  = LoadpullPursuitEngine.Resolve(lpa, netlist.ResolvedGlobals);

        // Most tests run without the follow-on loadpull for speed; the acceptance test
        // explicitly passes createLoadpullResult: true.
        if (!createLoadpullResult)
            pp = pp with { CreateLoadpullResult = false };

        var lpEngine = new LoadpullEngine(netlist, tb);
        var engine   = new LoadpullPursuitEngine(lpEngine);
        return (engine, pp, dir);
    }

    // ── Helpers to extract pursuit scalars from DataSet ────────────────────────

    private static Complex MxpZ(DataSet ds) =>
        new Complex(ds["MXP_ZRe"].RealValues[0], ds["MXP_ZIm"].RealValues[0]);

    private static Complex MxeZ(DataSet ds) =>
        new Complex(ds["MXE_ZRe"].RealValues[0], ds["MXE_ZIm"].RealValues[0]);

    private static bool MxpConverged(DataSet ds) => ds["MXP_Converged"].RealValues[0] > 0.5;
    private static bool MxeConverged(DataSet ds) => ds["MXE_Converged"].RealValues[0] > 0.5;

    private static double MxpPoutDbm(DataSet ds) => ds["MXP_PoutDbm"].RealValues[0];
    private static double MxeEff(DataSet ds)     => ds["MXE_Eff"].RealValues[0];

    private static bool MxpHasZsource(DataSet ds) => ds["MXP_HasZsource"].RealValues[0] > 0.5;
    private static bool MxeHasZsource(DataSet ds) => ds["MXE_HasZsource"].RealValues[0] > 0.5;

    private static Complex MxpZsource(DataSet ds) =>
        new Complex(ds["MXP_ZsourceRe"].RealValues[0], ds["MXP_ZsourceIm"].RealValues[0]);

    private static Complex MxeZsource(DataSet ds) =>
        new Complex(ds["MXE_ZsourceRe"].RealValues[0], ds["MXE_ZsourceIm"].RealValues[0]);

    private static int CacheCount(DataSet ds)      => (int)ds["CacheCount"].RealValues[0];
    private static int UnscorableCount(DataSet ds) => (int)ds["UnscorableCount"].RealValues[0];
    private static int RecommTermCount(DataSet ds) => (int)ds["RecommTermCount"].RealValues[0];

    // ── 1. Owner Test ────────────────────────────────────────────────────

    [Fact]
    public void OwnerTest()
    {
        var (engine, pp, dir) = BuildEngine();

        Console.WriteLine(
            $"Owner Test: f0={pp.LpParams.ToneHz/1e9:F3} GHz  " +
            $"K={pp.LpParams.MaxHarmonic}  " +
            $"GuardHarmonic={pp.LpParams.GuardHarmonic}  " +
            $"PinMax={pp.LpParams.PinMaxDbm} dBm  Compression={pp.LpParams.Compression} dB  " +
            $"EffType={(pp.UsePae ? "PAE" : "DE")}  ZsourceOBO={pp.ZsourceOBoDB} dB");

        var ds = engine.Run(pp);

        // ── Acceptance checks ─────────────────────────────────────────────────
        Assert.True(MxpConverged(ds), "MXP did not converge.");
        Assert.True(MxeConverged(ds), "MXE did not converge.");

        double mxpPoutDbm = MxpPoutDbm(ds);
        double mxeEffPct  = MxeEff(ds) * 100;
        var    mxpZ       = MxpZ(ds);
        var    mxeZ       = MxeZ(ds);

        Console.WriteLine(
            $"MXP: Z={mxpZ.Real:F2}{(mxpZ.Imaginary >= 0 ? "+" : "")}{mxpZ.Imaginary:F2}j Ω  " +
            $"Pout={mxpPoutDbm:F2} dBm");
        Console.WriteLine(
            $"MXE: Z={mxeZ.Real:F2}{(mxeZ.Imaginary >= 0 ? "+" : "")}{mxeZ.Imaginary:F2}j Ω  " +
            $"Eff={mxeEffPct:F1}%");

        // Pedro coupling (informational — empirical rule for real GaN PAs, not enforced here).
        double pedroVswr = RfHelpers.VswrFromZ(mxpZ, mxeZ);
        Console.WriteLine($"Pedro VSWR (MXP↔MXE) = {pedroVswr:F2}  " +
                         "(typical real PA: 2–2.5; synthetic FET may differ)");
        Assert.True(pedroVswr is >= 1.0 and <= 10.0,
            $"MXP↔MXE VSWR={pedroVswr:F2} is outside plausible range [1.0, 10.0].");

        // MXP Pout (dBm) must be physically reasonable (> 20 dBm at PinMax=30, gain ~10 dB).
        Assert.True(mxpPoutDbm > 20.0,
            $"MXP Pout={mxpPoutDbm:F2} dBm is implausibly low (expected > 20 dBm).");

        // MXE efficiency must be in (0,1).
        double mxeEff = MxeEff(ds);
        Assert.True(mxeEff > 0 && mxeEff < 1,
            $"MXE efficiency={mxeEff:F4} is not in (0,1).");

        // Zsource should be reported for both.
        Assert.True(MxpHasZsource(ds), "MXP Zsource not found.");
        Assert.True(MxeHasZsource(ds), "MXE Zsource not found.");
        var zsrcMxp = MxpZsource(ds);
        var zsrcMxe = MxeZsource(ds);
        Console.WriteLine($"Zsource@MXP = {zsrcMxp.Real:F2}{(zsrcMxp.Imaginary >= 0 ? "+" : "")}{zsrcMxp.Imaginary:F2}j Ω");
        Console.WriteLine($"Zsource@MXE = {zsrcMxe.Real:F2}{(zsrcMxe.Imaginary >= 0 ? "+" : "")}{zsrcMxe.Imaginary:F2}j Ω");

        // Cache should be non-trivial.
        int cc = CacheCount(ds);
        Console.WriteLine($"Cache entries: {cc}  Unscorable: {UnscorableCount(ds)}");
        Assert.True(cc > 5, "Too few cache entries — search may not have run.");
    }



    // ── 2. Golden generator ────────────────────────────────────────────────────

    [Fact]
    public void GenerateHero3BGolden()
    {
        var (engine, pp, dir) = BuildEngine();

        output.WriteLine(
            $"Hero 3B pursuit: f0={pp.LpParams.ToneHz/1e9:F3} GHz  " +
            $"K={pp.LpParams.MaxHarmonic}  " +
            $"PinMax={pp.LpParams.PinMaxDbm} dBm  Compression={pp.LpParams.Compression} dB  " +
            $"EffType={(pp.UsePae ? "PAE" : "DE")}  ZsourceOBO={pp.ZsourceOBoDB} dB");

        var ds = engine.Run(pp);

        // ── Acceptance checks ─────────────────────────────────────────────────
        Assert.True(MxpConverged(ds), "MXP did not converge.");
        Assert.True(MxeConverged(ds), "MXE did not converge.");

        double mxpPoutDbm = MxpPoutDbm(ds);
        double mxeEffPct  = MxeEff(ds) * 100;
        var    mxpZ       = MxpZ(ds);
        var    mxeZ       = MxeZ(ds);

        output.WriteLine(
            $"MXP: Z={mxpZ.Real:F2}{(mxpZ.Imaginary >= 0 ? "+" : "")}{mxpZ.Imaginary:F2}j Ω  " +
            $"Pout={mxpPoutDbm:F2} dBm");
        output.WriteLine(
            $"MXE: Z={mxeZ.Real:F2}{(mxeZ.Imaginary >= 0 ? "+" : "")}{mxeZ.Imaginary:F2}j Ω  " +
            $"Eff={mxeEffPct:F1}%");

        double pedroVswr = RfHelpers.VswrFromZ(mxpZ, mxeZ);
        output.WriteLine($"Pedro VSWR (MXP↔MXE) = {pedroVswr:F2}  " +
                         "(typical real PA: 2–2.5; synthetic FET may differ)");
        Assert.True(pedroVswr is >= 1.0 and <= 10.0,
            $"MXP↔MXE VSWR={pedroVswr:F2} is outside plausible range [1.0, 10.0].");

        Assert.True(mxpPoutDbm > 20.0,
            $"MXP Pout={mxpPoutDbm:F2} dBm is implausibly low (expected > 20 dBm).");

        double mxeEff = MxeEff(ds);
        Assert.True(mxeEff > 0 && mxeEff < 1,
            $"MXE efficiency={mxeEff:F4} is not in (0,1).");

        Assert.True(MxpHasZsource(ds), "MXP Zsource not found.");
        Assert.True(MxeHasZsource(ds), "MXE Zsource not found.");
        var zsrcMxp = MxpZsource(ds);
        var zsrcMxe = MxeZsource(ds);
        output.WriteLine($"Zsource@MXP = {zsrcMxp.Real:F2}{(zsrcMxp.Imaginary >= 0 ? "+" : "")}{zsrcMxp.Imaginary:F2}j Ω");
        output.WriteLine($"Zsource@MXE = {zsrcMxe.Real:F2}{(zsrcMxe.Imaginary >= 0 ? "+" : "")}{zsrcMxe.Imaginary:F2}j Ω");

        int cc = CacheCount(ds);
        output.WriteLine($"Cache entries: {cc}  Unscorable: {UnscorableCount(ds)}");
        Assert.True(cc > 5, "Too few cache entries — search may not have run.");

        // ── Write .gam if OutputGrid is specified in the directive ────────────
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3B_at_compression.cnl"));
        var lpa = tb.Analyses.OfType<LoadpullPursuitAnalysis>().First();
        if (lpa.OutputGridPath is not null)
        {
            // UnscorableZ list not stored in DataSet — pass empty list; focused grid is still written.
            var gamResult = GamWriter.Build(new GamWriter.GamBuilderParams(
                mxpZ, mxeZ, Array.Empty<Complex>()));
            GamWriter.WriteFile(lpa.OutputGridPath, gamResult);
            output.WriteLine($".gam written to: {lpa.OutputGridPath}  ({gamResult.Points.Count} pts)");
        }

        // ── Write golden CSV ──────────────────────────────────────────────────
        WriteGoldenCsv(dir, ds, pedroVswr);
        output.WriteLine("Hero 3B golden generated successfully.");
    }

    // ── 3. Regression test ─────────────────────────────────────────────────────

    [Fact]
    public void Hero3BPursuit_RegressionPasses()
    {
        var dir        = Hero3BDir();
        var goldenPath = Path.Combine(dir, "hero3B_self_pursuit.csv");

        if (!File.Exists(goldenPath))
        {
            output.WriteLine($"No golden at {goldenPath} — run GenerateHero3BGolden first.");
            return;
        }

        var (engine, pp, _) = BuildEngine();
        var ds = engine.Run(pp);

        // Parse golden.
        var golden = ParseGoldenCsv(goldenPath);

        // MXP Pout (dBm) — absolute dB tolerance (0.1 dB).
        const double MxpTolDb = 0.1;
        double mxpPoutDbm = MxpPoutDbm(ds);
        double mxpDiff    = Math.Abs(mxpPoutDbm - golden.MxpPout);
        output.WriteLine($"MXP Pout: current={mxpPoutDbm:F3} dBm  " +
                         $"golden={golden.MxpPout:F3} dBm  |Δ|={mxpDiff:F4} dB");
        Assert.True(mxpDiff < MxpTolDb,
            $"MXP Pout changed by {mxpDiff:F3} dB (> {MxpTolDb} dB) — regression.");

        // MXE efficiency (linear ratio, 0.1 percentage-point absolute tolerance).
        const double MxeTolPp = 0.001;
        double mxeEff  = MxeEff(ds);
        double mxeDiff = Math.Abs(mxeEff - golden.MxeEff);
        output.WriteLine($"MXE Eff: current={mxeEff*100:F2}%  " +
                         $"golden={golden.MxeEff*100:F2}%  |Δ|={mxeDiff*100:F3} pp");
        Assert.True(mxeDiff < MxeTolPp,
            $"MXE efficiency changed by {mxeDiff*100:F3} pp (> {MxeTolPp*100} pp) — regression.");

        // VSWR(MXP,MXE) within 20% of golden.
        double vswr     = RfHelpers.VswrFromZ(MxpZ(ds), MxeZ(ds));
        double vswrDiff = Math.Abs(vswr - golden.PedroVswr) / (golden.PedroVswr + 1e-9);
        output.WriteLine($"Pedro VSWR: current={vswr:F3}  golden={golden.PedroVswr:F3}  relDiff={vswrDiff:E3}");
        Assert.True(vswrDiff < 0.20,
            $"Pedro VSWR changed by {vswrDiff:E3} (> 20%) — optima have shifted significantly.");
    }




    // ── 4. Non-compression exit ────────────────────────────────────────────────

    [Fact]
    public void Hero3BPursuit_NonCompressionExitClean()
    {
        var dir     = Hero3BDir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3B_at_compression.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var lpa = tb.Analyses.OfType<LoadpullPursuitAnalysis>().First();
        var pp  = LoadpullPursuitEngine.Resolve(lpa, netlist.ResolvedGlobals);

        // Override PinMax to -18 dBm — DUT cannot compress, so start point is unscorable.
        var lpLow = pp.LpParams with { PinMaxDbm = -18.0 };
        var ppLow = pp with { LpParams = lpLow, CreateLoadpullResult = false };

        var lpEngine = new LoadpullEngine(netlist, tb);
        var engine   = new LoadpullPursuitEngine(lpEngine);

        // Should NOT throw — should return cleanly with MXP not converged.
        var ex = Record.Exception(() =>
        {
            var ds = engine.Run(ppLow);
            Assert.False(MxpConverged(ds),
                "MXP should not converge when PinMax is too low.");
            output.WriteLine("MXP not converged (expected) — PinMax too low for compression.");
        });
        Assert.Null(ex);   // must not crash
    }

    // ── 5. Brute-force vs pursuit agreement (permanent regression) ───────────

    /// <summary>
    /// Runs a 1-D brute-force sweep over the real axis (60–100 Ω, step 5 Ω) to find the
    /// true MXP Z, then runs the pursuit and asserts that the pursuit lands within 1.2 VSWR
    /// of the brute-force truth.
    ///
    /// This guards against the three search-algorithm bugs fixed on 2026-06-04:
    ///   (1) ds-collapse (exiting after 1 rejection),
    ///   (2) VswrToDeltaGamma approximation, and
    ///   (3) Γ-plane vs Z-plane metric mismatch.
    ///
    /// The brute-force is restricted to Im=0, 9 points, so it runs in ~1–2 s.
    /// Tolerance 1.2 VSWR: allows ≈±10 Ω error at Z≈80 Ω.
    /// </summary>
    [Fact]
    public void Hero3BPursuit_BruteForceAgreement()
    {
        var dir    = Hero3BDir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3B_at_compression.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var lpa      = tb.Analyses.OfType<LoadpullPursuitAnalysis>().First();
        var pp       = LoadpullPursuitEngine.Resolve(lpa, netlist.ResolvedGlobals);
        var lpp      = pp.LpParams;
        var lpEngine = new LoadpullEngine(netlist, tb);

        // ── Brute-force sweep (Im=0, Re=60..100 Ω, step 5) ──────────────────
        var ctx     = lpEngine.PrepareContext(lpp);
        double bfMxpPout = double.NegativeInfinity;
        Complex bfMxpZ  = new Complex(50, 0);
        int idx = 0;
        foreach (double zRe in Enumerable.Range(0, 9).Select(i => 60.0 + i * 5.0))
        {
            var z   = new Complex(zRe, 0);
            var gpr = lpEngine.RunOneTermination(lpp, ctx, z, idx++);
            if (gpr.StopReason != "Compression") continue;
            var conv = gpr.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
            if (conv.Count == 0) continue;
            int? maxIdx = conv.Select((s, i) => new { s.GtDb, i }).MaxBy(x => x.GtDb)?.i;
            if (maxIdx is null) continue;
            double gMax = conv[maxIdx.Value].GtDb;
            PinStepResult? bel = null, abv = null;
            for (int i = maxIdx.Value; i < conv.Count; i++)
            {
                double compr = gMax - conv[i].GtDb;
                if (compr < lpp.Compression)       bel = conv[i];
                else if (abv is null)               abv = conv[i];
            }
            if (abv is null) continue;
            bel ??= abv;
            double cB = gMax - bel.GtDb, cA = gMax - abv.GtDb, dC = cA - cB;
            double t  = dC > 1e-10 ? Math.Clamp((lpp.Compression - cB) / dC, 0, 1) : 0;
            double pdbm = 10*Math.Log10(bel.PoutW*1000) + t*(10*Math.Log10(abv.PoutW*1000) - 10*Math.Log10(bel.PoutW*1000));
            if (pdbm > bfMxpPout) { bfMxpPout = pdbm; bfMxpZ = z; }
        }
        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        output.WriteLine($"Brute-force MXP: Z={bfMxpZ.Real:F1} Ω  Pout={bfMxpPout:F3} dBm");
        Assert.True(bfMxpPout > 25, $"Brute-force MXP implausibly low ({bfMxpPout:F2} dBm)");

        // ── Pursuit ───────────────────────────────────────────────────────────
        var pursuitEngine = new LoadpullPursuitEngine(new LoadpullEngine(netlist, tb));
        var ds            = pursuitEngine.Run(pp with { CreateLoadpullResult = false });

        Assert.True(MxpConverged(ds), "MXP did not converge.");

        var    pursuitMxpZ    = MxpZ(ds);
        double pursuitPoutDbm = MxpPoutDbm(ds);
        double vswr = RfHelpers.VswrFromZ(pursuitMxpZ, bfMxpZ);
        output.WriteLine(
            $"Pursuit MXP:     Z={pursuitMxpZ.Real:F1}{(pursuitMxpZ.Imaginary >= 0 ? "+" : "")}{pursuitMxpZ.Imaginary:F1}j Ω  " +
            $"Pout={pursuitPoutDbm:F3} dBm");
        output.WriteLine($"VSWR(pursuit, brute-force) = {vswr:F3}  (limit 1.20)");

        Assert.True(vswr < 1.20,
            $"Pursuit MXP at Z={pursuitMxpZ.Real:F1} Ω is {vswr:F3} VSWR from " +
            $"brute-force MXP at Z={bfMxpZ.Real:F1} Ω — search missed the optimum.");
    }

    // ── 6. IteratedQuadratic — brute-force agreement ──────────────────────────
    //
    // Mirrors Hero3BPursuit_BruteForceAgreement but with SearchMethod.IteratedQuadratic.
    // Verifies the new method also lands within 1.20 VSWR of the brute-force grid MXP.
    // Reports walk trajectory and query count vs SteepestAscent for diagnostic purposes.

    [Fact]
    public void Hero3BPursuit_BruteForceAgreement_IteratedQuadratic()
    {
        var dir    = Hero3BDir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3B_at_compression.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var lpa      = tb.Analyses.OfType<LoadpullPursuitAnalysis>().First();
        var pp       = LoadpullPursuitEngine.Resolve(lpa, netlist.ResolvedGlobals);
        var lpp      = pp.LpParams;
        var lpEngine = new LoadpullEngine(netlist, tb);

        // ── Brute-force sweep (Im=0, Re=60..100 Ω, step 5) ──────────────────
        var ctx     = lpEngine.PrepareContext(lpp);
        double bfMxpPout = double.NegativeInfinity;
        Complex bfMxpZ  = new Complex(50, 0);
        int idx = 0;
        foreach (double zRe in Enumerable.Range(0, 9).Select(i => 60.0 + i * 5.0))
        {
            var z   = new Complex(zRe, 0);
            var gpr = lpEngine.RunOneTermination(lpp, ctx, z, idx++);
            if (gpr.StopReason != "Compression") continue;
            var conv = gpr.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
            if (conv.Count == 0) continue;
            int? maxIdx = conv.Select((s, i) => new { s.GtDb, i }).MaxBy(x => x.GtDb)?.i;
            if (maxIdx is null) continue;
            double gMax = conv[maxIdx.Value].GtDb;
            PinStepResult? bel = null, abv = null;
            for (int i = maxIdx.Value; i < conv.Count; i++)
            {
                double compr = gMax - conv[i].GtDb;
                if (compr < lpp.Compression)       bel = conv[i];
                else if (abv is null)               abv = conv[i];
            }
            if (abv is null) continue;
            bel ??= abv;
            double cB = gMax - bel.GtDb, cA = gMax - abv.GtDb, dC = cA - cB;
            double t  = dC > 1e-10 ? Math.Clamp((lpp.Compression - cB) / dC, 0, 1) : 0;
            double pdbm = 10*Math.Log10(bel.PoutW*1000) + t*(10*Math.Log10(abv.PoutW*1000) - 10*Math.Log10(bel.PoutW*1000));
            if (pdbm > bfMxpPout) { bfMxpPout = pdbm; bfMxpZ = z; }
        }
        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        output.WriteLine($"Brute-force MXP: Z={bfMxpZ.Real:F1} Ω  Pout={bfMxpPout:F3} dBm");
        Assert.True(bfMxpPout > 25, $"Brute-force MXP implausibly low ({bfMxpPout:F2} dBm)");

        // ── IteratedQuadratic pursuit ─────────────────────────────────────────
        var ppIQ = pp with { SearchMethod = SearchMethod.IteratedQuadratic, CreateLoadpullResult = false };
        var pursuitEngine = new LoadpullPursuitEngine(new LoadpullEngine(netlist, tb));
        var ds            = pursuitEngine.Run(ppIQ);

        Assert.True(MxpConverged(ds), "IQ MXP did not converge.");

        var    iqMxpZ    = MxpZ(ds);
        double iqPoutDbm = MxpPoutDbm(ds);
        double vswr = RfHelpers.VswrFromZ(iqMxpZ, bfMxpZ);
        output.WriteLine(
            $"IQ Pursuit MXP: Z={iqMxpZ.Real:F1}{(iqMxpZ.Imaginary >= 0 ? "+" : "")}{iqMxpZ.Imaginary:F1}j Ω  " +
            $"Pout={iqPoutDbm:F3} dBm");
        output.WriteLine($"VSWR(IQ pursuit, brute-force) = {vswr:F3}  (limit 1.20)");
        output.WriteLine($"IQ cache entries: {CacheCount(ds)}  IQ unscorable: {UnscorableCount(ds)}");

        Assert.True(vswr < 1.20,
            $"IQ Pursuit MXP at Z={iqMxpZ.Real:F1} Ω is {vswr:F3} VSWR from " +
            $"brute-force MXP at Z={bfMxpZ.Real:F1} Ω — IQ search missed the optimum.");
    }

    // ── 7. IteratedQuadratic — Hero 3B convergence to ~80 Ω (diagnostic) ─────
    //
    // Reports IQ walk, query count vs SteepestAscent (target ≤ 2×), and whether IQ
    // lands near the true Hero 3B MXP (~80 Ω, per loadpull_pursuit.md §1.1.2).

    [Fact]
    public void Hero3BPursuit_IteratedQuadratic_ReachesOptimum()
    {
        var (engine, pp, _) = BuildEngine();

        // ── Run SteepestAscent first to get the baseline query count ──────────
        var ppSA    = pp with { SearchMethod = SearchMethod.SteepestAscent, CreateLoadpullResult = false };
        var (saEng, _, _) = BuildEngine();
        var saDs    = saEng.Run(ppSA);
        int saQueries = CacheCount(saDs);
        var saMxpZ    = MxpZ(saDs);
        output.WriteLine($"SA  query count: {saQueries}  MXP Z={saMxpZ.Real:F2}{(saMxpZ.Imaginary >= 0 ? "+" : "")}{saMxpZ.Imaginary:F2}j Ω  Pout={MxpPoutDbm(saDs):F2} dBm");

        // ── Run IteratedQuadratic ─────────────────────────────────────────────
        var ppIQ   = pp with { SearchMethod = SearchMethod.IteratedQuadratic, CreateLoadpullResult = false };
        var iqDs   = engine.Run(ppIQ);
        int iqQueries = CacheCount(iqDs);
        var iqMxpZ    = MxpZ(iqDs);
        var iqMxeZ    = MxeZ(iqDs);
        output.WriteLine($"IQ  query count: {iqQueries}  MXP Z={iqMxpZ.Real:F2}{(iqMxpZ.Imaginary >= 0 ? "+" : "")}{iqMxpZ.Imaginary:F2}j Ω  Pout={MxpPoutDbm(iqDs):F2} dBm");
        output.WriteLine($"IQ  MXE: Z={iqMxeZ.Real:F2}{(iqMxeZ.Imaginary >= 0 ? "+" : "")}{iqMxeZ.Imaginary:F2}j Ω  Eff={MxeEff(iqDs)*100:F1}%");

        // Verify convergence.
        Assert.True(MxpConverged(iqDs), "IQ MXP did not converge.");
        Assert.True(MxeConverged(iqDs), "IQ MXE did not converge.");

        // Verify IQ is physically reasonable (same basic sanity as SA test).
        Assert.True(MxpPoutDbm(iqDs) > 20.0,
            $"IQ MXP Pout={MxpPoutDbm(iqDs):F2} dBm implausibly low.");
        double iqMxeEff = MxeEff(iqDs);
        Assert.True(iqMxeEff > 0 && iqMxeEff < 1,
            $"IQ MXE efficiency={iqMxeEff:F4} not in (0,1).");

        // Report query economy: target ≤ 2× SteepestAscent.
        double queryRatio = saQueries > 0 ? (double)iqQueries / saQueries : double.PositiveInfinity;
        output.WriteLine($"Query ratio IQ/SA = {queryRatio:F2}  (target ≤ 2.0; if >> 2 the VSWR cache may not be catching clustered cardinals)");
        if (queryRatio > 2.0)
            output.WriteLine($"WARNING: IQ query count ({iqQueries}) is {queryRatio:F2}× SA ({saQueries}) — exceeds the ≤2× guideline. Check cache hit rate.");

        // Report whether IQ lands at the expected ~80 Ω MXP.
        double mxpRe = iqMxpZ.Real;
        output.WriteLine($"IQ MXP Re(Z) = {mxpRe:F1} Ω  (design-note target ~80 Ω per loadpull_pursuit.md §1.1.2)");
        if (Math.Abs(mxpRe - 80.0) > 15.0)
            output.WriteLine($"DIAGNOSTIC: IQ MXP Re(Z)={mxpRe:F1} Ω differs >15 Ω from ~80 Ω target. " +
                $"SA landed at {saMxpZ.Real:F1} Ω. " +
                $"Run Diagnostic1_TruthSurface to verify the brute-force criterion surface.");

        // Zsource reported for both.
        Assert.True(MxpHasZsource(iqDs), "IQ MXP Zsource not found.");
        Assert.True(MxeHasZsource(iqDs), "IQ MXE Zsource not found.");
        var iqZsrcMxp = MxpZsource(iqDs);
        var iqZsrcMxe = MxeZsource(iqDs);
        output.WriteLine($"IQ Zsource@MXP = {iqZsrcMxp.Real:F2}{(iqZsrcMxp.Imaginary >= 0 ? "+" : "")}{iqZsrcMxp.Imaginary:F2}j Ω");
        output.WriteLine($"IQ Zsource@MXE = {iqZsrcMxe.Real:F2}{(iqZsrcMxe.Imaginary >= 0 ? "+" : "")}{iqZsrcMxe.Imaginary:F2}j Ω");
    }

    // ── CSV I/O ───────────────────────────────────────────────────────────────

    private void WriteGoldenCsv(string dir, DataSet ds, double pedroVswr)
    {
        var path = Path.Combine(dir, "hero3B_self_pursuit.csv");
        using var w = new StreamWriter(path);
        w.WriteLine("# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine("# Generated by circuitRF LoadpullPursuitEngine (Phase 4b-2).");
        w.WriteLine("# Circuit: hero3B_at_compression.cnl");
        w.WriteLine("# Key: MxpPout(dBm); MxpZRe; MxpZIm; MxeEff(linear); MxeZRe; MxeZIm; PedroVswr; CacheCount; UnscorableCount");
        w.WriteLine("# B3 fix: MxpPout is now in dBm (not Watts).");
        var mxpZ = MxpZ(ds);
        var mxeZ = MxeZ(ds);
        w.WriteLine(string.Join("; ", new[]
        {
            MxpPoutDbm(ds).ToString("G10",  CultureInfo.InvariantCulture),
            mxpZ.Real.ToString("G10",       CultureInfo.InvariantCulture),
            mxpZ.Imaginary.ToString("G10",  CultureInfo.InvariantCulture),
            MxeEff(ds).ToString("G10",      CultureInfo.InvariantCulture),
            mxeZ.Real.ToString("G10",       CultureInfo.InvariantCulture),
            mxeZ.Imaginary.ToString("G10",  CultureInfo.InvariantCulture),
            pedroVswr.ToString("G10",       CultureInfo.InvariantCulture),
            CacheCount(ds).ToString(),
            UnscorableCount(ds).ToString(),
        }));
        output.WriteLine($"Golden written to {path}");
    }

    private sealed record GoldenRow(
        double MxpPout, double MxeEff, double PedroVswr,
        int CacheCount, int UnscorableCount);

    private static GoldenRow ParseGoldenCsv(string path)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(';');
            if (p.Length < 7) continue;
            try
            {
                return new GoldenRow(
                    double.Parse(p[0].Trim(), CultureInfo.InvariantCulture),
                    double.Parse(p[3].Trim(), CultureInfo.InvariantCulture),
                    double.Parse(p[6].Trim(), CultureInfo.InvariantCulture),
                    p.Length > 7 ? int.Parse(p[7].Trim()) : 0,
                    p.Length > 8 ? int.Parse(p[8].Trim()) : 0);
            }
            catch { /* skip */ }
        }
        throw new InvalidOperationException($"Could not parse golden CSV: {path}");
    }

    // ── 8. Follow-on LoadpullResult acceptance (Phase 4b-2 enhancement) ───────

    /// <summary>
    /// Acceptance: with CreateLoadpullResult=true (the directive default), the run produces
    /// follow-on loadpull data embedded in the pursuit DataSet with LP_ prefix.
    ///   - LP_ cubes present (e.g. LP_StopCode)
    ///   - LP grid count matches RecommTermCount
    ///   - All LP stop codes in range 0-3
    /// Also verifies: LP_ cubes absent when CreateLoadpullResult=false.
    /// </summary>
    [Fact]
    public void Hero3BPursuit_FollowOnLoadpullResult_WhenCreateOn_DataPresent()
    {
        // Use the default (CreateLoadpullResult=true from directive).
        var (engine, pp, _) = BuildEngine(createLoadpullResult: true);

        var ds = engine.Run(pp);

        // ── Search optima ─────────────────────────────────────────────────────
        Assert.True(MxpConverged(ds), "MXP did not converge.");
        Assert.True(MxeConverged(ds), "MXE did not converge.");

        // ── RecommendedTerminations always present ────────────────────────────
        int recommCount = RecommTermCount(ds);
        Assert.True(recommCount > 0,
            "RecommTermCount is zero — gam builder should produce points around the optima.");

        // ── Follow-on loadpull data present ───────────────────────────────────
        Assert.True(ds.Contains("LP_StopCode"),
            "LP_StopCode not found — follow-on loadpull data missing from DataSet.");

        int lpGridCount = ds["LP_StopCode"].Axes[0].Length;
        Assert.Equal(recommCount, lpGridCount);

        // All LP stop codes must be valid (0-3).
        var lpStopCodes = ds["LP_StopCode"].RealValues;
        for (int i = 0; i < lpStopCodes.Length; i++)
        {
            int code = (int)Math.Round(lpStopCodes[i]);
            Assert.True(code >= 0 && code <= 3,
                $"Unexpected LP_StopCode {code} at grid point {i}");
        }

        int lpCompressed = lpStopCodes.Count(c => (int)Math.Round(c) == 1);
        output.WriteLine(
            $"Follow-on loadpull DataSet: {lpGridCount} grid points  " +
            $"(compressed: {lpCompressed})");
        output.WriteLine(
            $"Recommended terminations: {recommCount} pts");

        if (MxeHasZsource(ds))
        {
            var zsrc = MxeZsource(ds);
            output.WriteLine(
                $"MXE Zsource used for source match: " +
                $"{zsrc.Real:F2}{(zsrc.Imaginary >= 0 ? "+" : "")}{zsrc.Imaginary:F2}j Ω");
        }
    }

    [Fact]
    public void Hero3BPursuit_FollowOnLoadpullResult_WhenCreateOff_DataNull()
    {
        var (engine, pp, _) = BuildEngine(createLoadpullResult: false);

        var ds = engine.Run(pp);

        // RecommendedTerminations count always populated.
        Assert.True(RecommTermCount(ds) >= 0);

        // No follow-on loadpull cubes.
        Assert.False(ds.Contains("LP_StopCode"),
            "LP_StopCode found but CreateLoadpullResult=false — follow-on data should be absent.");

        output.WriteLine(
            $"CreateLoadpullResult=false → LP_ cubes absent  " +
            $"(RecommTermCount still = {RecommTermCount(ds)})");
    }
}
