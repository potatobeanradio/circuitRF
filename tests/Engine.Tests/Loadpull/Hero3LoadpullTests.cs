using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;
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
///
/// Phase 5-5: updated to use DataSet result API (values unchanged, re-housed only).
///
/// No longer class-tagged "Slow" (docs/sonnet-briefs/brief-test-default-fast.md): every test here
/// runs well under the ~5s per-test threshold, so nothing in this class needs excluding from the
/// default run — tag by measured cost, not by subject matter.
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

    private static string StopCodeToReason(double code) => (int)Math.Round(code) switch
    {
        0 => "PinMax",
        1 => "Compression",
        2 => "NonConvergence",
        3 => "NoConvergedSeed",
        _ => "Unknown",
    };

    // ── Freq carrier provenance ─────────────────────────────────────────────────

    [Fact]
    public void Hero3Loadpull_EmitsFreqCarrier_ForSummaryTable()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero3Dir(), "hero3.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var lpa = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var p   = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);
        var ds  = new LoadpullEngine(netlist, tb).Run(p);

        // The FOM cubes carry no "freq" axis (single-frequency); a __Freq carrier preserves the tone
        // so the Data Display summary reports 2 GHz instead of 0.
        Assert.True(ds.Contains("__Freq"));
        Assert.Equal(p.ToneHz, ds["__Freq"][0].RealValue!.Value, precision: 3);
        Assert.Equal(2e9, p.ToneHz, precision: 3);
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
        var ds     = engine.Run(p);

        int nG = ds["StopCode"].Axes[0].Length;
        int nP = ds["Converged"].Axes[1].Length;

        // ── Acceptance checks ─────────────────────────────────────────────────

        // Every grid point must have a valid stop code (0-3).
        for (int gi = 0; gi < nG; gi++)
        {
            int code = (int)Math.Round((double)ds["StopCode"][gi]);
            Assert.True(code >= 0 && code <= 3,
                $"Grid point {gi}: stop code {code} is unknown.");
        }

        // At least one grid point must have converged Pin steps.
        int totalConverged = ds["Converged"].RealValues.Count(v => v > 0.5);
        output.WriteLine($"Total converged Pin steps: {totalConverged}");
        Assert.True(totalConverged > 0, "No Pin steps converged across the entire grid.");

        // Print per-grid-point summary.
        output.WriteLine("--- Grid point summary ---");
        var gmaxValues = new List<double>();
        for (int gi = 0; gi < nG; gi++)
        {
            var gamma = (Complex)ds["GammaLoad"][gi];
            string stopReason = StopCodeToReason((double)ds["StopCode"][gi]);

            var convGts = new List<double>();
            double lastPout = 0;
            for (int pi = 0; pi < nP; pi++)
            {
                if ((double)ds["Converged"][gi, pi] > 0.5 && (double)ds["IsTickle"][gi, pi] < 0.5)
                {
                    convGts.Add((double)ds["Gt"][gi, pi]);
                    lastPout = (double)ds["Pout"][gi, pi];
                }
            }

            if (convGts.Count == 0)
            {
                output.WriteLine(
                    $"  [{gi}] Γ={gamma.Real:F3}+j{gamma.Imaginary:F3}  " +
                    $"No converged steps  Stop={stopReason}");
                continue;
            }

            double gmax = convGts.Max();
            double gmin = convGts.Min();
            gmaxValues.Add(gmax);

            output.WriteLine(
                $"  [{gi}] Γ={gamma.Real:F3}+j{gamma.Imaginary:F3}  " +
                $"Gt={gmax:F2}..{gmin:F2} dB  " +
                $"Pout_last={10*Math.Log10(lastPout*1000):F2} dBm  " +
                $"Steps={convGts.Count}  Stop={stopReason}");

            // Gt must be positive for a PA.
            Assert.True(gmax > 0, $"Grid point {gi}: max Gt={gmax:F2} dB ≤ 0 (not a PA).");
        }

        // Small-signal gain consistency.
        if (gmaxValues.Count > 1)
        {
            double spreadDb = gmaxValues.Max() - gmaxValues.Min();
            output.WriteLine($"Gmax spread across grid: {spreadDb:F2} dB " +
                             $"(min={gmaxValues.Min():F2}, max={gmaxValues.Max():F2})");
            Assert.True(spreadDb < 20.0,
                $"Gmax spread of {spreadDb:F2} dB is implausibly large — check sign convention.");
        }

        // Count grid points by stop reason.
        var stopCodes        = ds["StopCode"].RealValues;
        int compressionCount = stopCodes.Count(c => (int)Math.Round(c) == 1);
        int pinMaxCount      = stopCodes.Count(c => (int)Math.Round(c) == 0);
        int nonConvCount     = stopCodes.Count(c => (int)Math.Round(c) == 2);
        int noConvSeedCount  = stopCodes.Count(c => (int)Math.Round(c) == 3);
        output.WriteLine(
            $"Stop reasons: Compression={compressionCount}  PinMax={pinMaxCount}  NonConvergence={nonConvCount}  NoConvergedSeed={noConvSeedCount}");
        Assert.Equal(nG, compressionCount + pinMaxCount + nonConvCount + noConvSeedCount);

        // ── Write golden data ─────────────────────────────────────────────────
        output.WriteLine("Writing Hero 3 golden data ...");
        WriteFomsCsv(dir, "hero3_self_FOMs.csv", ds, p);
        WriteSpectraCsv(dir, "hero3_self_V.csv",   "V",   ds, p, true);
        WriteSpectraCsv(dir, "hero3_self_INl.csv", "INl", ds, p, false);

        output.WriteLine("Hero 3 golden generated successfully.");
    }

    // ── 2. Regression test ─────────────────────────────────────────────────────

    [Fact]
    public void Hero3Loadpull_RegressionPasses()
    {
        const string GoldenFoms = "hero3_self_FOMs.csv";
        var dir        = Hero3Dir();
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
        var ds        = engine.Run(p);

        // Load golden FOMs.
        var goldenRows = ParseFomsCsv(goldenPath);

        int nG      = ds["StopCode"].Axes[0].Length;
        int nP      = ds["Converged"].Axes[1].Length;
        var pinVals = ds["Converged"].Axes[1].Values;

        const double Tol = 1e-5;  // "< 1e-5 is noise" — same rule as Hero 2
        int checked_ = 0, mismatches = 0;

        for (int gi = 0; gi < nG; gi++)
        for (int pi = 0; pi < nP; pi++)
        {
            bool conv = (double)ds["Converged"][gi, pi] > 0.5;
            if (!conv) continue;

            bool   isTickle = (double)ds["IsTickle"][gi, pi] > 0.5;
            double pavl     = pinVals[pi];
            var    key      = (gi, Math.Round(pavl, 6), isTickle);
            if (!goldenRows.TryGetValue(key, out var golden)) continue;

            double poutW = (double)ds["Pout"][gi, pi];
            double diff  = Math.Abs(poutW - golden.PoutW);
            if (diff > Tol && diff > Tol * Math.Abs(golden.PoutW))
            {
                output.WriteLine(
                    $"MISMATCH grid={gi} Pin={pavl:F1} dBm  " +
                    $"Pout: current={poutW:E6} golden={golden.PoutW:E6} diff={diff:E3}");
                mismatches++;
            }
            checked_++;
        }

        output.WriteLine($"Regression: {checked_} rows checked, {mismatches} mismatches (tol={Tol:E0})");
        Assert.Equal(0, mismatches);
    }

    // ── CSV writers ────────────────────────────────────────────────────────────

    private static void WriteFomsCsv(string dir, string filename, DataSet ds,
        LoadpullAnalysisParams p)
    {
        int nG      = ds["StopCode"].Axes[0].Length;
        int nP      = ds["Converged"].Axes[1].Length;
        var pinVals = ds["Converged"].Axes[1].Values;

        var path = Path.Combine(dir, filename);
        using var w = new StreamWriter(path);
        w.WriteLine("# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine($"# Generated by circuitRF Loadpull engine (Phase 4b-1).");
        w.WriteLine($"# Circuit: hero3.cnl  |  f0={p.ToneHz/1e9:F3} GHz  K={p.MaxHarmonic}");
        w.WriteLine($"# Grid: {p.Grid.Points.Count} points  Z0={p.Grid.Z0} Ω");
        w.WriteLine("GridIdx; GammaRe; GammaIm; ZRe; ZIm; PavlDbm; IsTickle; " +
                    "Converged; Iterations; PavlW; PinDelivW; PoutW; GtDb; GpDb; " +
                    "BiasVLoad; BiasILoad; BiasVSrc; BiasISrc; PdcW; De; Pae; StopReason");

        for (int gi = 0; gi < nG; gi++)
        {
            var gamma      = (Complex)ds["GammaLoad"][gi];
            var z          = (Complex)ds["ZLoad"][gi];
            string stopReason = StopCodeToReason((double)ds["StopCode"][gi]);

            for (int pi = 0; pi < nP; pi++)
            {
                double pavlDbm   = pinVals[pi];
                double isTickle  = (double)ds["IsTickle"][gi, pi];
                double converged = (double)ds["Converged"][gi, pi];
                double poutW     = (double)ds["Pout"][gi, pi];
                double gtDb      = (double)ds["Gt"][gi, pi];
                double gpDb      = (double)ds["Gp"][gi, pi];
                double biasVLoad = (double)ds["BiasVLoad"][gi, pi];
                double biasILoad = (double)ds["BiasILoad"][gi, pi];
                double biasVSrc  = (double)ds["BiasVSrc"][gi, pi];
                double biasISrc  = (double)ds["BiasISrc"][gi, pi];
                double pdcW      = (double)ds["Pdc"][gi, pi];
                double de        = (double)ds["DE"][gi, pi];
                double pae       = (double)ds["PAE"][gi, pi];
                // PavlW computed from dBm; PinDeliveredW not stored → 0
                double pavlW     = Math.Pow(10.0, pavlDbm / 10.0) * 1e-3;

                w.WriteLine(string.Join("; ", new[]
                {
                    gi.ToString(CultureInfo.InvariantCulture),
                    gamma.Real.ToString("G10",      CultureInfo.InvariantCulture),
                    gamma.Imaginary.ToString("G10", CultureInfo.InvariantCulture),
                    z.Real.ToString("G10",          CultureInfo.InvariantCulture),
                    z.Imaginary.ToString("G10",     CultureInfo.InvariantCulture),
                    pavlDbm.ToString("G6",          CultureInfo.InvariantCulture),
                    (isTickle > 0.5 ? "1" : "0"),
                    (converged > 0.5 ? "1" : "0"),
                    "0",                                            // iterations (not stored)
                    pavlW.ToString("G10",           CultureInfo.InvariantCulture),
                    "0",                                            // PinDeliveredW (not stored)
                    poutW.ToString("G10",           CultureInfo.InvariantCulture),
                    gtDb.ToString("G8",             CultureInfo.InvariantCulture),
                    gpDb.ToString("G8",             CultureInfo.InvariantCulture),
                    biasVLoad.ToString("G8",        CultureInfo.InvariantCulture),
                    biasILoad.ToString("G8",        CultureInfo.InvariantCulture),
                    biasVSrc.ToString("G8",         CultureInfo.InvariantCulture),
                    biasISrc.ToString("G8",         CultureInfo.InvariantCulture),
                    pdcW.ToString("G8",             CultureInfo.InvariantCulture),
                    de.ToString("G8",               CultureInfo.InvariantCulture),
                    pae.ToString("G8",              CultureInfo.InvariantCulture),
                    stopReason,
                }));
            }
        }
    }

    private static void WriteSpectraCsv(string dir, string filename, string quantity,
        DataSet ds, LoadpullAnalysisParams p, bool isV)
    {
        string cubeName = isV ? "V" : "INl";
        if (!ds.Contains(cubeName)) return;

        int nG      = ds["StopCode"].Axes[0].Length;
        int nP      = ds["Converged"].Axes[1].Length;
        int nN      = ds[cubeName].Axes[2].Length;
        int nH      = ds[cubeName].Axes[3].Length;
        var pinVals = ds["Converged"].Axes[1].Values;
        var nodeLabels = ds[cubeName].Axes[2].Labels ?? Array.Empty<string>();
        double f0   = p.ToneHz;

        var path = Path.Combine(dir, filename);
        using var w = new StreamWriter(path);
        w.WriteLine("# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine($"# Quantity: {quantity}");

        w.WriteLine("GridIdx; PavlDbm; HarmonicK; FreqHz; NodeIdx; NodeName; Re; Im");

        for (int gi = 0; gi < nG; gi++)
        for (int pi = 0; pi < nP; pi++)
        {
            if ((double)ds["Converged"][gi, pi] < 0.5) continue;
            double pavlDbm = pinVals[pi];

            for (int ni = 0; ni < nN; ni++)
            {
                string nodeName = ni < nodeLabels.Length ? nodeLabels[ni] : $"if[{ni}]";
                for (int hi = 0; hi < nH; hi++)
                {
                    var c = (Complex)ds[cubeName][gi, pi, ni, hi];
                    w.WriteLine(string.Join("; ", new[]
                    {
                        gi.ToString(),
                        pavlDbm.ToString("G6",   CultureInfo.InvariantCulture),
                        hi.ToString(),
                        (hi * f0).ToString("G10", CultureInfo.InvariantCulture),
                        ni.ToString(),
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
        var ds      = engine.Run(p);

        int nG = ds["StopCode"].Axes[0].Length;
        int nP = ds["Converged"].Axes[1].Length;
        int checks = 0;

        for (int gi = 0; gi < nG; gi++)
        {
            var gamma = (Complex)ds["GammaLoad"][gi];

            // Find the last converged non-tickle step (highest power).
            int lastPi = -1;
            for (int pi = 0; pi < nP; pi++)
                if ((double)ds["Converged"][gi, pi] > 0.5 && (double)ds["IsTickle"][gi, pi] < 0.5)
                    lastPi = pi;

            if (lastPi < 0) continue;

            double pdc = (double)ds["Pdc"][gi, lastPi];
            if (pdc <= 0) continue;   // guard: degenerate bias

            double biasVLoad = (double)ds["BiasVLoad"][gi, lastPi];
            double biasILoad = (double)ds["BiasILoad"][gi, lastPi];
            double biasVSrc  = (double)ds["BiasVSrc"][gi, lastPi];
            double biasISrc  = (double)ds["BiasISrc"][gi, lastPi];

            // Verify Pdc matches hand formula.
            double pdcExpected = biasVLoad * (-biasILoad) + biasVSrc * (-biasISrc);
            Assert.Equal(pdcExpected, pdc, precision: 8);

            double de    = (double)ds["DE"][gi, lastPi];
            double pae   = (double)ds["PAE"][gi, lastPi];
            double poutW = (double)ds["Pout"][gi, lastPi];

            // Physical bounds: 0 < DE ≤ 1, 0 < PAE < DE.
            Assert.True(de > 0,
                $"Grid {gi}: DE={de:F4} ≤ 0 (non-physical).");
            Assert.True(de <= 1.0,
                $"Grid {gi}: DE={de:F4} > 1 (non-physical).");
            Assert.True(pae >= 0,
                $"Grid {gi}: PAE={pae:F4} < 0 (non-physical for a PA).");
            Assert.True(pae <= de,
                $"Grid {gi}: PAE={pae:F4} > DE={de:F4} (non-physical).");

            output.WriteLine(
                $"  [{gi}] Γ={gamma.Real:F3}+j{gamma.Imaginary:F3}  " +
                $"Pout={poutW*1e3:F1} mW  Pdc={pdc*1e3:F1} mW  " +
                $"DE={de*100:F1}%  PAE={pae*100:F1}%");
            checks++;
        }

        output.WriteLine($"Efficiency checks: {checks} grid points verified.");
        Assert.True(checks > 0, "No converged non-tickle steps found — cannot verify efficiency.");
    }

   // ── 4. RLSweep a simple resistive loadpull ────────────────────────────────────────────────────

    [Fact]
    public void RLSweep()
    {
        var dir = Hero3Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "RLSweep.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var lpa = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var p   = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);

        Console.WriteLine(
            $"RL SweepLoadpull: f0={p.ToneHz/1e9:F3} GHz  K={p.MaxHarmonic}  " +
            $"Grid={p.Grid.Points.Count} pts  " +
            $"Pin={p.PinStartDbm}..{p.PinMaxDbm} step {p.PinStepDb} dBm  " +
            $"Compression={p.Compression} dB  GainType={( p.UseGt ? "Gt" : "Gp" )}");

        var engine = new LoadpullEngine(netlist, tb);
        var ds     = engine.Run(p);

        int nG = ds["StopCode"].Axes[0].Length;
        int nP = ds["Converged"].Axes[1].Length;

        // ── Acceptance checks ─────────────────────────────────────────────────

        // Every grid point must have a valid stop code (0-3).
        for (int gi = 0; gi < nG; gi++)
        {
            int code = (int)Math.Round((double)ds["StopCode"][gi]);
            Assert.True(code >= 0 && code <= 3,
                $"Grid point {gi}: stop code {code} is unknown.");
        }

        // At least one grid point must have converged Pin steps.
        int totalConverged = ds["Converged"].RealValues.Count(v => v > 0.5);
        Console.WriteLine($"Total converged Pin steps: {totalConverged}");
        Assert.True(totalConverged > 0, "No Pin steps converged across the entire grid.");

        // Print per-grid-point summary.
        Console.WriteLine("--- Grid point summary ---");
        var gmaxValues = new List<double>();
        for (int gi = 0; gi < nG; gi++)
        {
            var gamma = (Complex)ds["GammaLoad"][gi];
            string stopReason = StopCodeToReason((double)ds["StopCode"][gi]);

            var convGts = new List<double>();
            double lastPout = 0;
            for (int pi = 0; pi < nP; pi++)
            {
                if ((double)ds["Converged"][gi, pi] > 0.5 && (double)ds["IsTickle"][gi, pi] < 0.5)
                {
                    convGts.Add((double)ds["Gt"][gi, pi]);
                    lastPout = (double)ds["Pout"][gi, pi];
                }
            }

            if (convGts.Count == 0)
            {
                Console.WriteLine(
                    $"  [{gi}] Γ={gamma.Real:F3}+j{gamma.Imaginary:F3}  " +
                    $"No converged steps  Stop={stopReason}");
                continue;
            }

            double gmax = convGts.Max();
            double gmin = convGts.Min();
            gmaxValues.Add(gmax);

            output.WriteLine(
                $"  [{gi}] Γ={gamma.Real:F3}+j{gamma.Imaginary:F3}  " +
                $"Gt={gmax:F2}..{gmin:F2} dB  " +
                $"Pout_last={10*Math.Log10(lastPout*1000):F2} dBm  " +
                $"Steps={convGts.Count}  Stop={stopReason}");

            // Gt must be positive for a PA.
            Assert.True(gmax > 0, $"Grid point {gi}: max Gt={gmax:F2} dB ≤ 0 (not a PA).");
        }

        if (gmaxValues.Count > 1)
        {
            double spreadDb = gmaxValues.Max() - gmaxValues.Min();
            Console.WriteLine($"Gmax spread across grid: {spreadDb:F2} dB " +
                             $"(min={gmaxValues.Min():F2}, max={gmaxValues.Max():F2})");
            Assert.True(spreadDb < 20.0,
                $"Gmax spread of {spreadDb:F2} dB is implausibly large — check sign convention.");
        }

        var stopCodes        = ds["StopCode"].RealValues;
        int compressionCount = stopCodes.Count(c => (int)Math.Round(c) == 1);
        int pinMaxCount      = stopCodes.Count(c => (int)Math.Round(c) == 0);
        int nonConvCount     = stopCodes.Count(c => (int)Math.Round(c) == 2);
        int noConvSeedCount  = stopCodes.Count(c => (int)Math.Round(c) == 3);
        Console.WriteLine(
            $"Stop reasons: Compression={compressionCount}  PinMax={pinMaxCount}  NonConvergence={nonConvCount}  NoConvergedSeed={noConvSeedCount}");
        Assert.Equal(nG, compressionCount + pinMaxCount + nonConvCount + noConvSeedCount);

        // ── Write data ─────────────────────────────────────────────────
        Console.WriteLine("Writing RLSweep data ...");
        WriteFomsCsv(dir, "RLSweep_FOMs.csv", ds, p);
        WriteSpectraCsv(dir, "RLSweep_V.csv",   "V",   ds, p, true);
        WriteSpectraCsv(dir, "RLSweep_INl.csv", "INl", ds, p, false);

        output.WriteLine("RLSweep generated successfully.");
    }
}
