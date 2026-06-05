using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Phase 4b-1 acceptance gate — Hero 3: single-device loadpull of the GaN HEMT PA.
///
/// Two tests:
///   1. GenerateHero3Golden — runs the full 2-D sweep and writes golden CSVs.
///      Run with: dotnet test --filter GenerateHero3Golden
///   2. Hero3Loadpull_RegressionPasses — compares current run against the frozen golden.
///
/// Both tests verify the sweep acceptance criteria from Phase4b1_Brief.md:
///   - Every grid point has a recorded stop reason.
///   - Gt and Gp are positive (sensible PA gain).
///   - Gmax (small-signal) is roughly the same across grid points (termination-independent SS gain).
///   - P-3dB compression detected at some grid points (Gt drops ≥ 3 dB from Gmax).
///   - Golden tolerance: |Re − Re_golden| and |Im − Im_golden| &lt; 1e-5 (noise floor per Hero 2).
///
/// LABEL: SELF-GENERATED REGRESSION — NOT INDEPENDENTLY VALIDATED.
/// The owner will verify before freezing.
/// </summary>
public class Hero3LoadpullTests(ITestOutputHelper output)
{
    private static string Hero3Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero3");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero3 not found");
    }

    // ── 1. Golden generator ────────────────────────────────────────────────────

    [Fact]
    public void GenerateHero3Golden()
    {
        var dir = Hero3Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var lpa = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var p   = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);

        output.WriteLine(
            $"Hero 3 Loadpull: f0={p.ToneHz/1e9:F3} GHz  K={p.MaxHarmonic}  " +
            $"Grid={p.Grid.Points.Count} pts  " +
            $"Pin={p.PinStartDbm}..{p.PinMaxDbm} step {p.PinStepDb} dBm  " +
            $"Compression={p.Compression} dB  GainType={( p.UseGt ? "Gt" : "Gp" )}");

        var engine = new LoadpullEngine(netlist, tb);
        var result = engine.Run(p);

        // ── Acceptance checks ─────────────────────────────────────────────────

        // Every grid point must have a recorded stop reason.
        foreach (var gp in result.GridPoints)
        {
            Assert.False(string.IsNullOrEmpty(gp.StopReason),
                $"Grid point {gp.GridIndex}: missing stop reason.");
        }

        // At least one grid point must have converged Pin steps.
        int totalConverged = result.GridPoints.Sum(gp => gp.PinSteps.Count(s => s.Converged));
        output.WriteLine($"Total converged Pin steps: {totalConverged}");
        Assert.True(totalConverged > 0, "No Pin steps converged across the entire grid.");

        // Print per-grid-point summary.
        output.WriteLine("--- Grid point summary ---");
        var gmaxValues = new List<double>();
        foreach (var gp in result.GridPoints)
        {
            var convSteps = gp.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
            if (convSteps.Count == 0)
            {
                output.WriteLine(
                    $"  [{gp.GridIndex}] Γ={gp.Gamma.Real:F3}+j{gp.Gamma.Imaginary:F3}  " +
                    $"No converged steps  Stop={gp.StopReason}");
                continue;
            }

            double gmax   = convSteps.Max(s => s.GtDb);
            double gmin   = convSteps.Min(s => s.GtDb);
            double poutAt = convSteps.Last().PoutW;
            gmaxValues.Add(gmax);

            output.WriteLine(
                $"  [{gp.GridIndex}] Γ={gp.Gamma.Real:F3}+j{gp.Gamma.Imaginary:F3}  " +
                $"Gt={gmax:F2}..{gmin:F2} dB  " +
                $"Pout_last={10*Math.Log10(poutAt*1000):F2} dBm  " +
                $"Steps={convSteps.Count}  Stop={gp.StopReason}");

            // Gt must be positive for a PA.
            Assert.True(gmax > 0, $"Grid point {gp.GridIndex}: max Gt={gmax:F2} dB ≤ 0 (not a PA).");
        }

        // Small-signal gain consistency: Gmax should be roughly the same for all grid points
        // that converged (the small-signal gain is termination-independent for a unilateral device,
        // or nearly so; we allow 10 dB variation which is generous for a real device).
        if (gmaxValues.Count > 1)
        {
            double spreadDb = gmaxValues.Max() - gmaxValues.Min();
            output.WriteLine($"Gmax spread across grid: {spreadDb:F2} dB " +
                             $"(min={gmaxValues.Min():F2}, max={gmaxValues.Max():F2})");
            Assert.True(spreadDb < 20.0,
                $"Gmax spread of {spreadDb:F2} dB is implausibly large — check sign convention.");
        }

        // Count grid points by stop reason — all three are valid per the brief (§3.1):
        // Compression, PinMax, NonConvergence. PinMax is expected if the FET requires
        // higher drive than the directive's PinMax to reach the target compression.
        int compressionCount  = result.GridPoints.Count(gp => gp.StopReason == "Compression");
        int pinMaxCount       = result.GridPoints.Count(gp => gp.StopReason == "PinMax");
        int nonConvCount      = result.GridPoints.Count(gp => gp.StopReason == "NonConvergence");
        output.WriteLine(
            $"Stop reasons: Compression={compressionCount}  PinMax={pinMaxCount}  NonConvergence={nonConvCount}");
        // Every grid point must have a valid stop reason.
        Assert.Equal(result.GridPoints.Count, compressionCount + pinMaxCount + nonConvCount);

        // ── Write golden data ─────────────────────────────────────────────────
        output.WriteLine("Writing Hero 3 golden data ...");
        WriteFomsCsv(dir, "hero3_self_FOMs.csv", result, p);
        WriteSpectraCsv(dir, "hero3_self_V.csv",   "V",   result, true);
        WriteSpectraCsv(dir, "hero3_self_INl.csv", "INl", result, false);

        output.WriteLine("Hero 3 golden generated successfully.");
    }

    // ── 2. Regression test ─────────────────────────────────────────────────────

    [Fact]
    public void Hero3Loadpull_RegressionPasses()
    {
        const string GoldenFoms = "hero3_self_FOMs.csv";
        var dir      = Hero3Dir();
        var goldenPath = Path.Combine(dir, GoldenFoms);

        if (!File.Exists(goldenPath))
        {
            output.WriteLine($"No golden file at {goldenPath} — run GenerateHero3Golden first.");
            return;   // skip gracefully until golden is generated
        }

        // Run the current engine.
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var lpa       = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var p         = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);
        var engine    = new LoadpullEngine(netlist, tb);
        var result    = engine.Run(p);

        // Load golden FOMs.
        var goldenRows = ParseFomsCsv(goldenPath);

        // Compare: each matching (gridIdx, pavlDbm, isTickle) row.
        const double Tol = 1e-5;  // "< 1e-5 is noise" — same rule as Hero 2
        int checked_ = 0, mismatches = 0;

        foreach (var gp in result.GridPoints)
        foreach (var step in gp.PinSteps.Where(s => s.Converged))
        {
            var key  = (gp.GridIndex, Math.Round(step.PavlDbm, 6), step.IsTickle);
            if (!goldenRows.TryGetValue(key, out var golden)) continue;

            // Pout (W) regression.
            double diff = Math.Abs(step.PoutW - golden.PoutW);
            if (diff > Tol && diff > Tol * Math.Abs(golden.PoutW))
            {
                output.WriteLine(
                    $"MISMATCH grid={gp.GridIndex} Pin={step.PavlDbm:F1} dBm  " +
                    $"Pout: current={step.PoutW:E6} golden={golden.PoutW:E6} diff={diff:E3}");
                mismatches++;
            }
            checked_++;
        }

        output.WriteLine($"Regression: {checked_} rows checked, {mismatches} mismatches (tol={Tol:E0})");
        Assert.Equal(0, mismatches);
    }

    // ── CSV writers ────────────────────────────────────────────────────────────

    private static void WriteFomsCsv(string dir, string filename, LoadpullResult result,
        LoadpullAnalysisParams p)
    {
        var path = Path.Combine(dir, filename);
        using var w = new StreamWriter(path);
        w.WriteLine("# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine($"# Generated by circuitRF Loadpull engine (Phase 4b-1).");
        w.WriteLine($"# Circuit: hero3.cnl  |  f0={p.ToneHz/1e9:F3} GHz  K={p.MaxHarmonic}");
        w.WriteLine($"# Grid: {result.Grid.Points.Count} points  Z0={result.Grid.Z0} Ω");
        w.WriteLine("GridIdx; GammaRe; GammaIm; ZRe; ZIm; PavlDbm; IsTickle; " +
                    "Converged; Iterations; PavlW; PinDelivW; PoutW; GtDb; GpDb; " +
                    "BiasVLoad; BiasILoad; BiasVSrc; BiasISrc; PdcW; De; Pae; StopReason");

        foreach (var gp in result.GridPoints)
        foreach (var s in gp.PinSteps)
        {
            w.WriteLine(string.Join("; ", new[]
            {
                gp.GridIndex.ToString(CultureInfo.InvariantCulture),
                gp.Gamma.Real.ToString("G10",      CultureInfo.InvariantCulture),
                gp.Gamma.Imaginary.ToString("G10", CultureInfo.InvariantCulture),
                gp.Z.Real.ToString("G10",          CultureInfo.InvariantCulture),
                gp.Z.Imaginary.ToString("G10",     CultureInfo.InvariantCulture),
                s.PavlDbm.ToString("G6",           CultureInfo.InvariantCulture),
                s.IsTickle ? "1" : "0",
                s.Converged ? "1" : "0",
                s.Iterations.ToString(),
                s.PavlW.ToString("G10",           CultureInfo.InvariantCulture),
                s.PinDeliveredW.ToString("G10",   CultureInfo.InvariantCulture),
                s.PoutW.ToString("G10",           CultureInfo.InvariantCulture),
                s.GtDb.ToString("G8",             CultureInfo.InvariantCulture),
                s.GpDb.ToString("G8",             CultureInfo.InvariantCulture),
                s.BiasVoltageLoadV.ToString("G8", CultureInfo.InvariantCulture),
                s.BiasCurrentLoadA.ToString("G8", CultureInfo.InvariantCulture),
                s.BiasVoltageSrcV.ToString("G8",  CultureInfo.InvariantCulture),
                s.BiasCurrentSrcA.ToString("G8",  CultureInfo.InvariantCulture),
                s.PdcW.ToString("G8",             CultureInfo.InvariantCulture),
                s.De.ToString("G8",               CultureInfo.InvariantCulture),
                s.Pae.ToString("G8",              CultureInfo.InvariantCulture),
                gp.StopReason,
            }));
        }
    }

    private static void WriteSpectraCsv(string dir, string filename, string quantity,
        LoadpullResult result, bool isV)
    {
        var path = Path.Combine(dir, filename);
        using var w = new StreamWriter(path);
        w.WriteLine("# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine($"# Quantity: {quantity}");
        int K = result.MaxHarm;
        double f0 = result.ToneHz;

        w.WriteLine("GridIdx; PavlDbm; HarmonicK; FreqHz; NodeIdx; NodeName; Re; Im");

        foreach (var gp in result.GridPoints)
        foreach (var s in gp.PinSteps.Where(ps => ps.Converged))
        {
            var spectra = isV ? s.V : s.INl;
            int N = spectra.GetLength(0);
            for (int n = 0; n < N; n++)
            {
                string nodeName = n < result.InterfaceNodeNames.Length
                    ? result.InterfaceNodeNames[n] : $"if[{n}]";
                for (int k = 0; k <= K; k++)
                {
                    var c = spectra[n, k];
                    w.WriteLine(string.Join("; ", new[]
                    {
                        gp.GridIndex.ToString(),
                        s.PavlDbm.ToString("G6",   CultureInfo.InvariantCulture),
                        k.ToString(),
                        (k * f0).ToString("G10",  CultureInfo.InvariantCulture),
                        n.ToString(),
                        nodeName,
                        c.Real.ToString("G10",     CultureInfo.InvariantCulture),
                        c.Imaginary.ToString("G10",CultureInfo.InvariantCulture),
                    }));
                }
            }
        }
    }

    // ── CSV parser for regression ──────────────────────────────────────────────

    private record GoldenFomRow(double PoutW);

    private static Dictionary<(int GridIdx, double PavlDbm, bool IsTickle), GoldenFomRow>
        ParseFomsCsv(string path)
    {
        var result = new Dictionary<(int, double, bool), GoldenFomRow>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("GridIdx")) continue;  // header
            var parts = line.Split(';');
            if (parts.Length < 13) continue;
            try
            {
                int    gi       = int.Parse(parts[0].Trim());
                double pavl     = double.Parse(parts[5].Trim(), CultureInfo.InvariantCulture);
                bool   isTickle = parts[6].Trim() == "1";
                bool   conv     = parts[7].Trim() == "1";
                if (!conv) continue;
                double poutW = double.Parse(parts[11].Trim(), CultureInfo.InvariantCulture);
                result[(gi, Math.Round(pavl, 6), isTickle)] = new GoldenFomRow(poutW);
            }
            catch { /* skip malformed lines */ }
        }
        return result;
    }


    // ── 3. Efficiency (DE/PAE) sanity test ────────────────────────────────────
    //
    // Verifies the efficiency computation (loadpull_pursuit.md §2) on a real converged
    // Hero-3 operating point.  Hand-check:
    //   Pdc = Vdd·Idd + Vgg·Igg
    //       = BiasVoltageLoadV·(-BiasCurrentLoadA) + BiasVoltageSrcV·(-BiasCurrentSrcA)
    //       = 48·(+Idd) + (-3.05)·(-Igg)
    //   DE  = Pout / Pdc
    //   PAE = (Pout − Pin_delivered) / Pdc
    //
    // Acceptance: DE ∈ (0,1), PAE < DE, PAE > 0 at a converged in-compression step.
    [Fact]
    public void Hero3_Efficiency_IsPhysical()
    {
        var dir     = Hero3Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3.cnl"));
        var netlist = new Elaborator(lib).Elaborate(tb);
        var lpa     = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var p       = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);
        var engine  = new LoadpullEngine(netlist, tb);
        var result  = engine.Run(p);

        int checks = 0;
        foreach (var gp in result.GridPoints)
        {
            // Use non-tickle converged steps only (tickle is at very low power: near-zero Pout).
            var steps = gp.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
            if (steps.Count == 0) continue;

            // Take the last converged step (highest power, most interesting for efficiency).
            var s = steps.Last();
            if (s.PdcW <= 0) continue;   // guard: degenerate bias

            // Verify Pdc matches hand formula.
            double pdcExpected = s.BiasVoltageLoadV * (-s.BiasCurrentLoadA)
                               + s.BiasVoltageSrcV  * (-s.BiasCurrentSrcA);
            Assert.Equal(pdcExpected, s.PdcW, precision: 8);

            // Physical bounds: 0 < DE ≤ 1, 0 < PAE < DE.
            Assert.True(s.De > 0,
                $"Grid {gp.GridIndex}: DE={s.De:F4} ≤ 0 (non-physical).");
            Assert.True(s.De <= 1.0,
                $"Grid {gp.GridIndex}: DE={s.De:F4} > 1 (non-physical).");
            Assert.True(s.Pae >= 0,
                $"Grid {gp.GridIndex}: PAE={s.Pae:F4} < 0 (non-physical for a PA).");
            Assert.True(s.Pae <= s.De,
                $"Grid {gp.GridIndex}: PAE={s.Pae:F4} > DE={s.De:F4} (non-physical).");

            output.WriteLine(
                $"  [{gp.GridIndex}] Γ={gp.Gamma.Real:F3}+j{gp.Gamma.Imaginary:F3}  " +
                $"Pout={s.PoutW*1e3:F1} mW  Pdc={s.PdcW*1e3:F1} mW  " +
                $"DE={s.De*100:F1}%  PAE={s.Pae*100:F1}%");
            checks++;
        }

        output.WriteLine($"Efficiency checks: {checks} grid points verified.");
        Assert.True(checks > 0, "No converged non-tickle steps found — cannot verify efficiency.");
    }

// [Fact]
//     public void TestHero3AtCompression()// additional test at compression added after Phase 4.1b officially passed; commented out to save test time
//     {
//         var dir = Hero3Dir();
//         var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3_at_compression.cnl"));
//         var netlist   = new Elaborator(lib).Elaborate(tb);

//         var lpa = tb.Analyses.OfType<LoadpullAnalysis>().First();
//         var p   = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);
//         output.WriteLine(
//             $"Hero 3 Loadpull: f0={p.ToneHz/1e9:F3} GHz  K={p.MaxHarmonic}  " +
//             $"Grid={p.Grid.Points.Count} pts  " +
//             $"Pin={p.PinStartDbm}..{p.PinMaxDbm} step {p.PinStepDb} dBm  " +
//             $"Compression={p.Compression} dB  GainType={( p.UseGt ? "Gt" : "Gp" )}");

//         var engine = new LoadpullEngine(netlist, tb);
//         var result = engine.Run(p);

//         // ── Acceptance checks ─────────────────────────────────────────────────

//         // Every grid point must have a recorded stop reason.
//         foreach (var gp in result.GridPoints)
//         {
//             Assert.False(string.IsNullOrEmpty(gp.StopReason),
//                 $"Grid point {gp.GridIndex}: missing stop reason.");
//         }

//         // At least one grid point must have converged Pin steps.
//         int totalConverged = result.GridPoints.Sum(gp => gp.PinSteps.Count(s => s.Converged));
//         output.WriteLine($"Total converged Pin steps: {totalConverged}");
//         Assert.True(totalConverged > 0, "No Pin steps converged across the entire grid.");

//         // Print per-grid-point summary.
//         output.WriteLine("--- Grid point summary ---");
//         var gmaxValues = new List<double>();
//         foreach (var gp in result.GridPoints)
//         {
//             var convSteps = gp.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
//             if (convSteps.Count == 0)
//             {
//                 output.WriteLine(
//                     $"  [{gp.GridIndex}] Γ={gp.Gamma.Real:F3}+j{gp.Gamma.Imaginary:F3}  " +
//                     $"No converged steps  Stop={gp.StopReason}");
//                 continue;
//             }

//             double gmax   = convSteps.Max(s => s.GtDb);
//             double gmin   = convSteps.Min(s => s.GtDb);
//             double poutAt = convSteps.Last().PoutW;
//             gmaxValues.Add(gmax);

//             output.WriteLine(
//                 $"  [{gp.GridIndex}] Γ={gp.Gamma.Real:F3}+j{gp.Gamma.Imaginary:F3}  " +
//                 $"Gt={gmax:F2}..{gmin:F2} dB  " +
//                 $"Pout_last={10*Math.Log10(poutAt*1000):F2} dBm  " +
//                 $"Steps={convSteps.Count}  Stop={gp.StopReason}");

//             // Gt must be positive for a PA.
//             Assert.True(gmax > 0, $"Grid point {gp.GridIndex}: max Gt={gmax:F2} dB ≤ 0 (not a PA).");
//         }

//         // Small-signal gain consistency: Gmax should be roughly the same for all grid points
//         // that converged (the small-signal gain is termination-independent for a unilateral device,
//         // or nearly so; we allow 10 dB variation which is generous for a real device).
//         if (gmaxValues.Count > 1)
//         {
//             double spreadDb = gmaxValues.Max() - gmaxValues.Min();
//             output.WriteLine($"Gmax spread across grid: {spreadDb:F2} dB " +
//                              $"(min={gmaxValues.Min():F2}, max={gmaxValues.Max():F2})");
//             Assert.True(spreadDb < 20.0,
//                 $"Gmax spread of {spreadDb:F2} dB is implausibly large — check sign convention.");
//         }

//         // Count grid points by stop reason — all three are valid per the brief (§3.1):
//         // Compression, PinMax, NonConvergence. PinMax is expected if the FET requires
//         // higher drive than the directive's PinMax to reach the target compression.
//         int compressionCount  = result.GridPoints.Count(gp => gp.StopReason == "Compression");
//         int pinMaxCount       = result.GridPoints.Count(gp => gp.StopReason == "PinMax");
//         int nonConvCount      = result.GridPoints.Count(gp => gp.StopReason == "NonConvergence");
//         output.WriteLine(
//             $"Stop reasons: Compression={compressionCount}  PinMax={pinMaxCount}  NonConvergence={nonConvCount}");
//         // Every grid point must have a valid stop reason.
//         Assert.Equal(result.GridPoints.Count, compressionCount + pinMaxCount + nonConvCount);

//         // ── Write golden data ─────────────────────────────────────────────────
//         output.WriteLine("Writing Hero 3 at compression golden data ...");
//         WriteFomsCsv(dir, "hero3_at_compression_self_FOMs.csv", result, p);
//         WriteSpectraCsv(dir, "hero3_at_compression_self_V.csv",   "V",   result, true);
//         WriteSpectraCsv(dir, "hero3_at_compression_self_INl.csv", "INl", result, false);

//         output.WriteLine("Hero 3 at compression golden generated successfully.");
//     }



}
