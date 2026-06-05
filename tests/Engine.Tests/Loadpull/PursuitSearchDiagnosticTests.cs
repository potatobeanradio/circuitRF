using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Diagnostic suite for the pursuit search quality.  Two focused tests that write
/// their full output to files in testdata/Hero3B/ (xUnit captures all console streams,
/// so files are the only reliable output path).
///
///   diag1_truth_surface.txt  — brute-force criterion surface
///   diag2_walk.txt           — instrumented pursuit walk
///
/// Run individually:
///   dotnet test --filter "DisplayName~Diagnostic1" -v minimal
///   dotnet test --filter "DisplayName~Diagnostic2" -v minimal
/// </summary>
public class PursuitSearchDiagnosticTests
{
    // ── Setup ─────────────────────────────────────────────────────────────────

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

    private static (LoadpullEngine LpEngine, LoadpullPursuitEngine.PursuitParams PP)
        BuildEngines()
    {
        var dir     = Hero3BDir();
        var cnlPath = Path.Combine(dir, "hero3B_at_compression.cnl");
        var (lib, tb) = CnlReader.ReadFile(cnlPath);
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var lpa = tb.Analyses.OfType<LoadpullPursuitAnalysis>().First();
        var pp  = LoadpullPursuitEngine.Resolve(lpa, netlist.ResolvedGlobals);
        var lp  = new LoadpullEngine(netlist, tb);
        return (lp, pp);
    }

    // ── Local criterion helpers ───────────────────────────────────────────────

    private static double WattsToDbm(double w) => 10.0 * Math.Log10(w * 1000.0);

    private static (double? PoutDbm, double? De) LocalExtract(GridPointResult gpr, double xdB)
    {
        if (gpr.StopReason != "Compression") return (null, null);
        var conv = gpr.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
        if (conv.Count == 0) return (null, null);
        // need the index for maxGain so that all lower index can be ignored
        int ? maxIndex = conv.Select((item, index) => new { item.GtDb, index }).MaxBy(x => x.GtDb)?.index;
        if (maxIndex == null) return (null, null);
        double gMax = conv[maxIndex.Value].GtDb;

        PinStepResult? below = null, above = null;
        for (int i = maxIndex.Value; i < conv.Count; i++)// only use power sweep points higher than the max Gain
        {
            double compr = gMax - conv[i].GtDb;
            if (compr < xdB)       below = conv[i];
            else if (above is null) above = conv[i];
        }
        if (above is null) return (null, null);
        below ??= above;
        double cB = gMax - below.GtDb, cA = gMax - above.GtDb;
        double t  = (cA - cB) > 1e-10 ? Math.Clamp((xdB - cB) / (cA - cB), 0.0, 1.0) : 0.0;
        double pB = WattsToDbm(below.PoutW), pA = WattsToDbm(above.PoutW);
        double de = below.De + t * (above.De - below.De);
        return (pB + t * (pA - pB), de);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DIAGNOSTIC 1 — Brute-force criterion surface
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Diagnostic1_TruthSurface()
    {
        var dir     = Hero3BDir();
        var outPath = Path.Combine(dir, "diag1_truth_surface.txt");
        using var f = new StreamWriter(outPath, append: false);
        void W(string s = "") => f.WriteLine(s);

        var (lpEngine, pp) = BuildEngines();
        var lpp = pp.LpParams;
        var ctx = lpEngine.PrepareContext(lpp);

        // Fine grid: real 40–120 Ω (step 5), imaginary ∈ {-10, 0, +10} Ω.
        double[] realParts = Enumerable.Range(0, 17).Select(i => 40.0 + i * 5.0).ToArray();
        double[] imagParts = { -10.0, 0.0, +10.0 };

        var results = new List<(Complex Z, double? PoutDbm, double? De, string Stop)>();
        int gridIdx = 0;
        foreach (double zIm in imagParts)
        foreach (double zRe in realParts)
        {
            var z   = new Complex(zRe, zIm);
            var gpr = lpEngine.RunOneTermination(lpp, ctx, z, gridIdx++);
            var (pout, de) = LocalExtract(gpr, lpp.Compression);
            results.Add((z, pout, de, gpr.StopReason));
        }

        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        // ── Print truth table ─────────────────────────────────────────────────
        W("═══════════════════════════════════════════════════════════════════");
        W(" DIAGNOSTIC 1 — Brute-force criterion surface (Hero 3B)");
        W($" Compression target = {lpp.Compression} dB  PinMax = {lpp.PinMaxDbm} dBm");
        W("═══════════════════════════════════════════════════════════════════");
        W($"{"ZRe",6} {"ZIm",6}  {"Pout@PxdB(dBm)",16}  {"DE@PxdB",10}  Stop");
        W(new string('─', 62));

        foreach (var (z, pout, de, stop) in results.OrderBy(r => r.Z.Imaginary).ThenBy(r => r.Z.Real))
        {
            string ps = pout.HasValue ? $"{pout.Value,8:F2}" : $"{"—",8}";
            string ds = de.HasValue   ? $"{de.Value * 100,8:F1}%" : $"{"—",9}";
            W($"{z.Real,6:F0} {z.Imaginary,+7:0.0;-0.0;0.0}  {ps,16}  {ds,10}  {stop}");
        }

        // ── Find and report true optima ───────────────────────────────────────
        var scored  = results.Where(r => r.PoutDbm.HasValue && r.De.HasValue).ToList();
        var trueMxp = scored.MaxBy(r => r.PoutDbm!.Value)!;
        var trueMxe = scored.MaxBy(r => r.De!.Value)!;

        W();
        W("─── True optima (from brute-force) ──────────────────────────────────");
        W($"True MXP: Z={trueMxp.Z.Real:F1}{(trueMxp.Z.Imaginary >= 0 ? "+" : "")}{trueMxp.Z.Imaginary:F1}j Ω  " +
          $"Pout@P{lpp.Compression}dB = {trueMxp.PoutDbm!.Value:F3} dBm");
        W($"True MXE: Z={trueMxe.Z.Real:F1}{(trueMxe.Z.Imaginary >= 0 ? "+" : "")}{trueMxe.Z.Imaginary:F1}j Ω  " +
          $"DE@P{lpp.Compression}dB = {trueMxe.De!.Value * 100:F2}%");

        // Pursuit landed at Z≈65Ω — compare with nearby grid points.
        W();
        W("─── Criterion value at pursuit-queried Z values ─────────────────────");
        foreach (double zRe in new[] { 50.0, 55.0, 60.0, 65.0, 70.0, 75.0, 80.0, 85.0, 90.0 })
        {
            var hit = results.FirstOrDefault(r => Math.Abs(r.Z.Real - zRe) < 1 && Math.Abs(r.Z.Imaginary) < 1);
            string ps = hit.PoutDbm.HasValue ? $"{hit.PoutDbm.Value:F3} dBm" : $"({hit.Stop})";
            string ds2 = hit.De.HasValue ? $"{hit.De.Value * 100:F2}%" : "—";
            string mark = (trueMxp.Z.Real == hit.Z.Real && trueMxp.Z.Imaginary == hit.Z.Imaginary) ? " ◄ TRUE MXP" : "";
            W($"  Z={zRe,5:F0}+0j Ω   Pout={ps}   DE={ds2}{mark}");
        }

        W();
        W($"Written to: testdata/Hero3B/{Path.GetFileName(outPath)}");

        // Sanity assertions.
        Assert.True(scored.Count > 5, $"Too few compressed points: {scored.Count}");
        Assert.True(trueMxp.PoutDbm!.Value > 25.0,
            $"Brute-force MXP Pout implausibly low: {trueMxp.PoutDbm.Value:F2} dBm");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DIAGNOSTIC 2 — Instrumented pursuit walk
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Diagnostic2_InstrumentedWalk()
    {
        var dir     = Hero3BDir();
        var outPath = Path.Combine(dir, "diag2_walk.txt");
        using var f = new StreamWriter(outPath, append: false);
        void W(string s = "") => f.WriteLine(s);

        var (lpEngine, pp) = BuildEngines();
        var lpp = pp.LpParams;
        var ctx = lpEngine.PrepareContext(lpp);

        // ── Wire up PursuitEngine with Log ────────────────────────────────────
        var logSb  = new StringBuilder();
        var logW   = new StringWriter(logSb);

        var queriedZ    = new List<Complex>();
        var queriedCrit = new List<double?>();

        // Criterion: exactly what LoadpullPursuitEngine.Query does (MXP).
        double? CriterionMxp(Complex z)
        {
            var gpr = lpEngine.RunOneTermination(lpp, ctx, z, queriedZ.Count);
            var (pout, _) = LocalExtract(gpr, lpp.Compression);
            queriedZ.Add(z);
            queriedCrit.Add(pout);
            return pout;
        }

        var baylis = new PursuitEngine
        {
            Dn                   = pp.Dn,
            DsInitial            = pp.Ds,
            ConvergenceThreshold = pp.ConvThreshold,
            MaxAscentSteps       = pp.MaxAscentSteps,
            Log                  = logW,
        };

        Complex startZ = lpp.Grid.Points.Count > 0
            ? lpp.Grid.Points[0].Z
            : new Complex(50, 0);

        var baResult = baylis.Run(startZ, CriterionMxp);

        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        logW.Flush();
        string[] logLines = logSb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // ── Write full log ────────────────────────────────────────────────────
        W("═══════════════════════════════════════════════════════════════════");
        W(" DIAGNOSTIC 2 — Instrumented pursuit walk (Hero 3B, MXP)");
        W($" Dn={pp.Dn}  DsInitial={pp.Ds}  ConvergenceThreshold={pp.ConvThreshold}  MaxSteps={pp.MaxAscentSteps}");
        W("═══════════════════════════════════════════════════════════════════");

        W();
        W("── PursuitEngine.Log (full) ─────────────────────────────────────────");
        foreach (var line in logLines)
            W(line);

        // ── ds trajectory analysis ────────────────────────────────────────────
        var accepts    = logLines.Where(l => l.Contains("[PE] Ascent.Accept:")).ToArray();
        var rejects    = logLines.Where(l => l.Contains("[PE] Ascent.Reject:")).ToArray();
        var terminates = logLines.Where(l => l.Contains("[PE] Ascent.Terminate:")).ToArray();
        var checks     = logLines.Where(l => l.Contains("[PE] Ascent.Check:")).ToArray();

        W();
        W("── ds trajectory summary ────────────────────────────────────────────");
        W($"DsInitial            = {pp.Ds}");
        W($"ConvergenceThreshold = {pp.ConvThreshold}");
        W($"MaxAscentSteps       = {pp.MaxAscentSteps}");
        W($"Accepts              = {accepts.Length}");
        W($"Rejects              = {rejects.Length}");
        W($"Terminate lines      = {terminates.Length}");
        W($"Loop checks          = {checks.Length}");

        W();
        W("── Hypothesis 1: ds collapse after first rejection ──────────────────");
        double dsAfterFirstReject = pp.Ds / 3.0;
        bool h1 = dsAfterFirstReject < pp.ConvThreshold;
        W($"DsInitial / 3 = {pp.Ds:G6} / 3 = {dsAfterFirstReject:G6}");
        W($"ConvergenceThreshold  = {pp.ConvThreshold:G6}");
        W($"{dsAfterFirstReject:G6} < {pp.ConvThreshold:G6}  →  loop exits IMMEDIATELY after first rejection: {h1}");
        W();
        // Parse each reject line for the ds transition.
        W("Reject ds transitions (from log):");
        foreach (var line in rejects)
        {
            int idx = line.IndexOf("ds:");
            if (idx >= 0)
            {
                string part = line[idx..].Trim();
                W($"  {part}");
                // Parse "ds: BEFORE → AFTER"
                var toks = part.Split(new[] { ' ', '→', ':' }, StringSplitOptions.RemoveEmptyEntries);
                // toks[0]="ds", toks[1]=before, toks[2]=after
                if (toks.Length >= 3 &&
                    double.TryParse(toks[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dsBefore) &&
                    double.TryParse(toks[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dsAfter))
                {
                    bool exitsNow = dsAfter < pp.ConvThreshold;
                    W($"    → ds_before={dsBefore:G4}  ds_after={dsAfter:G4}  " +
                      $"threshold={pp.ConvThreshold}  exits_immediately={exitsNow}  H1_CONFIRMED={h1 && exitsNow}");
                }
            }
        }

        // ── Hypothesis 2: step size accuracy ─────────────────────────────────
        W();
        W("── Hypothesis 2: VswrToDeltaGamma approximation error ───────────────");
        W("  (formula uses (vswr-1)/(vswr+1) = |Γ|-from-origin, ignores current Γ)");
        W($"  {"curZ",14}  {"|Γ|",6}  {"stepLen(approx)",16}  {"candZ",14}  {"trueVSWR",10}  {"intendedVSWR",12}  {"error",7}");
        W(new string('─', 90));

        // Reconstruct steps from log: Ascent.Step lines give curZ and candZ.
        var stepLines = logLines.Where(l => l.Contains("[PE] Ascent.Step:")).ToArray();
        foreach (var line in stepLines)
        {
            // Format: [PE] Ascent.Step: step=N ds=D stepLen=L curZ=... candZ=...
            string? Extract(string key)
            {
                int i = line.IndexOf(key + "=");
                if (i < 0) return null;
                int start = i + key.Length + 1;
                int end   = line.IndexOf(' ', start);
                return end < 0 ? line[start..] : line[start..end];
            }
            string? dsStr      = Extract("ds");
            string? stepLenStr = Extract("stepLen");
            string? curZStr    = Extract("curZ");
            string? candZStr   = Extract("candZ");
            if (dsStr is null || curZStr is null || candZStr is null) continue;

            double.TryParse(dsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double ds);
            double.TryParse(stepLenStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double stepLen);

            // Parse Z strings like "65.00+0.13j".
            Complex ParseZ(string s) {
                s = s.Replace("j", "").Trim();
                int plus = s.LastIndexOf('+'); int minus = s.LastIndexOf('-', s.Length - 1);
                int split = plus > 0 ? plus : minus > 0 ? minus : -1;
                if (split <= 0) return new Complex(double.Parse(s, CultureInfo.InvariantCulture), 0);
                double re = double.Parse(s[..split], NumberStyles.Float, CultureInfo.InvariantCulture);
                double im = double.Parse(s[split..], NumberStyles.Float, CultureInfo.InvariantCulture);
                return new Complex(re, im);
            }

            Complex curZ  = ParseZ(curZStr);
            Complex candZ = ParseZ(candZStr);
            double  gMag  = RfHelpers.Z2G(curZ / 50.0).Magnitude;
            double  trueVswr = RfHelpers.VswrFromZ(curZ, candZ);
            double  approxDG = (ds - 1.0) / (ds + 1.0);

            W($"  {curZ.Real,7:F2}{(curZ.Imaginary >= 0 ? "+" : "")}{curZ.Imaginary:F2}j  " +
              $"{gMag,6:F4}  " +
              $"{approxDG,16:F4}  " +
              $"{candZ.Real,7:F2}{(candZ.Imaginary >= 0 ? "+" : "")}{candZ.Imaginary:F2}j  " +
              $"{trueVswr,10:F4}  " +
              $"{ds,12:F4}  " +
              $"{trueVswr - ds,7:+0.000;-0.000}");
        }
        // Also check refinement cardinal steps.
        var refLines = logLines.Where(l => l.Contains("[PE] Refine.Cardinal:")).ToArray();
        if (refLines.Length > 0)
        {
            var refStartLine = logLines.FirstOrDefault(l => l.Contains("[PE] Refine.Start:"));
            W();
            W("  Refinement cardinal neighbours (Dn steps from final curZ):");
            W($"  {refStartLine ?? "(no refine start line)"}");
            foreach (var line in refLines) W($"    {line.Trim()}");
        }

        // ── Scoring check ─────────────────────────────────────────────────────
        W();
        W("── Scoring check: pursuit criterion vs independent re-run ────────────");
        W("  (same RunOneTermination + LocalExtract; criteria should be identical)");
        W($"  {"#",3}  {"Z",17}  {"pursuit_crit",14}  {"match?"}");
        W(new string('─', 55));
        for (int i = 0; i < queriedZ.Count && i < baResult.AllQueries.Count; i++)
        {
            var (qz, qv) = baResult.AllQueries[i];
            double? myV  = queriedCrit[i];
            bool match   = (myV.HasValue == qv.HasValue) &&
                           (!myV.HasValue || Math.Abs(myV.GetValueOrDefault() - qv.GetValueOrDefault()) < 1e-6);
            string note  = i == 0 ? "start"
                         : i == 1 ? "tangent N1"
                         : i == 2 ? "tangent N2"
                         : (accepts.Length + rejects.Length) > i - 3
                             ? $"ascent step {i - 2}"
                             : $"refine {i - 2 - accepts.Length - rejects.Length}";
            string qvStr = qv.HasValue ? $"{qv.Value:F4}" : "null";
            W($"  {i,3}  {qz.Real,7:F2}{(qz.Imaginary >= 0 ? "+" : "")}{qz.Imaginary:F2}j  " +
              $"{qvStr,14}  {(match ? "✓" : "✗ MISMATCH")}  {note}");
        }

        // ── Walk summary ──────────────────────────────────────────────────────
        W();
        W("── Search walk (queried Z in order) ─────────────────────────────────");
        W($"  {"#",3}  {"Z",17}  {"crit(dBm)",12}  note");
        for (int i = 0; i < queriedZ.Count; i++)
        {
            string note = i == 0 ? "start"
                        : i == 1 ? "tangent N1"
                        : i == 2 ? "tangent N2"
                        : i < 3 + accepts.Length + rejects.Length
                            ? $"ascent step {i - 2}"
                            : $"refine";
            string cv = queriedCrit[i].HasValue ? $"{queriedCrit[i]!.Value:F3}" : "null";
            W($"  {i,3}  {queriedZ[i].Real,7:F2}{(queriedZ[i].Imaginary >= 0 ? "+" : "")}{queriedZ[i].Imaginary:F2}j  {cv,12}  {note}");
        }

        W();
        W($"Pursuit optimum: Z={baResult.OptimumZ.Real:F2}{(baResult.OptimumZ.Imaginary >= 0 ? "+" : "")}{baResult.OptimumZ.Imaginary:F2}j Ω  " +
          $"value={baResult.OptimumValue:F4} dBm");
        W($"Total queries: {queriedZ.Count}  (start=1, tangent=2, ascent={accepts.Length + rejects.Length}, refine={refLines.Length})");
        W();
        W($"Written to: testdata/Hero3B/{Path.GetFileName(outPath)}");

        Assert.True(baResult.AllQueries.Count >= 3,
            "Expected at least 3 queries (start + 2 tangent neighbours)");
    }
}
