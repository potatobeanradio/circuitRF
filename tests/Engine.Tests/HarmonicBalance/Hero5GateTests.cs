using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Phase 4c Step 8 — the Hero 5 two-tone acceptance gate:
///   1. self-generated regression golden over the mixIndex axis (V/I at n_gate, n_drain);
///   2. the INDEPENDENT physics anchor — the IM3 3:1 slope at low drive (IM3 power rises ~3 dB per
///      1 dB of input while the carrier rises 1:1) — the check that the mixing is physically real,
///      not merely self-consistent;
///   3. the unequal-amplitude (V[1] != V[2]) guard on the general V_nTone path.
/// </summary>
public class Hero5GateTests(ITestOutputHelper output)
{
    private const double NoiseFloor = 1e-5;
    private static string Dir() => Hero5GoldenGenerator.Hero5Dir();

    // ── 1. Golden regression ─────────────────────────────────────────────────

    [Fact]
    public void Hero5_V_MatchesSelfGeneratedGolden()
        => RunAndCompare(compareV: true, label: "V");

    [Fact]
    public void Hero5_DeviceCurrent_MatchesSelfGeneratedGolden()
        => RunAndCompare(compareV: false, label: "I:M1");

    private void RunAndCompare(bool compareV, string label)
    {
        var dir   = Dir();
        var drain = LoadGolden(Path.Combine(dir, compareV ? "hero5_self_V_n_drain.csv"   : "hero5_self_INl_n_drain.csv"));
        var gate  = LoadGolden(Path.Combine(dir, compareV ? "hero5_self_V_n_gate.csv"    : "hero5_self_INl_n_gate.csv"));
        Assert.NotEmpty(drain);
        Assert.NotEmpty(gate);

        var ds = RunHero5("hero5.cnl",
            Hero5GoldenGenerator.GoldenStart, Hero5GoldenGenerator.GoldenStop, Hero5GoldenGenerator.GoldenStep);
        int maxOrder = (int)Math.Round(ds["MetaMixOrder"].RealValues[0]);
        var grid = new MixingGrid(maxOrder);
        int gateIdx  = NodeIdx(ds, "n_gate");
        int drainIdx = NodeIdx(ds, "n_drain");

        var sweepVals = ds["Converged"].Axes[0].Values;
        int nChecked = 0, nFail = 0;
        var fails = new List<string>();

        for (int si = 0; si < sweepVals.Length; si++)
        {
            double pin = sweepVals[si];
            for (int m = 0; m < grid.MixCount; m++)
            {
                var (k1, k2) = grid.ToneOf(m);
                foreach (var (lbl, idx, golden) in new[] { ("drain", drainIdx, drain), ("gate", gateIdx, gate) })
                {
                    var e = golden.FirstOrDefault(g =>
                        g.K1 == k1 && g.K2 == k2 && Math.Abs(g.Pave - pin) < 0.05);
                    if (e is null) continue;

                    // V uses the node-indexed cube; I: uses the named branch cube (no node index).
                    Complex sim = compareV
                        ? (Complex)ds["V"][si, idx, m]
                        : lbl == "drain"
                            ? (Complex)ds["I:M1:d"][si, m]
                            : (Complex)ds["I:M1:g"][si, m];
                    double simRe = sim.Real;
                    double simIm = m == 0 ? 0.0 : sim.Imaginary;

                    bool checkRe = Math.Abs(e.Re) >= NoiseFloor;
                    bool checkIm = Math.Abs(e.Im) >= NoiseFloor;
                    if (!checkRe && !checkIm) continue;
                    nChecked++;

                    double reDiff = checkRe ? Math.Abs(simRe - e.Re) : 0;
                    double imDiff = checkIm ? Math.Abs(simIm - e.Im) : 0;
                    double reTol  = checkRe ? Math.Max(1e-6, Math.Abs(e.Re) * 1e-4) : 0;
                    double imTol  = checkIm ? Math.Max(1e-6, Math.Abs(e.Im) * 1e-4) : 0;

                    if (reDiff > reTol || imDiff > imTol)
                    {
                        nFail++;
                        fails.Add($"  {lbl} ({k1},{k2}) Pin={pin:F1}: sim=({simRe:G6},{simIm:G6}) " +
                                  $"golden=({e.Re:G6},{e.Im:G6}) Δ=({reDiff:E2},{imDiff:E2})");
                    }
                }
            }
        }

        output.WriteLine($"[{label}] checked {nChecked} signal-bearing bins, {nFail} failures.");
        if (nFail > 0) { foreach (var f in fails) output.WriteLine(f); Assert.Fail($"{nFail}/{nChecked} {label} bins exceeded tolerance."); }
        Assert.True(nChecked > 0, $"No signal-bearing {label} bins — check mapping.");
    }

    // ── 2. IM3 3:1 slope — independent physics anchor ────────────────────────

    [Fact]
    public void Hero5_IM3_ThreeToOneSlope_AtLowDrive()
    {
        // The drive window must sit above the residual floor (~1e-9; the IM3 current must be well
        // above it to be resolved) yet below compression (where the 3:1 law holds). tol=1e-8 is the
        // achievable floor here — the huge Y dynamic range (1µΩ near-shorts → Y~1e6) caps Newton.
        // Slopes by least squares over the window for robustness against per-point IM3 noise.
        var ds = RunHero5("hero5.cnl", start: -18, stop: -12, step: 2, tol: 1e-8);
        var sweepVals = ds["Converged"].Axes[0].Values;
        for (int si = 0; si < sweepVals.Length; si++)
            Assert.True((double)ds["Converged"][si] > 0.5,
                $"point Pavl={sweepVals[si]} did not converge.");

        int n = sweepVals.Length;
        var carDbm = new double[n];
        var im3Dbm = new double[n];
        for (int i = 0; i < n; i++)
        {
            carDbm[i] = TwoToneMeasurements.PoutDbm(ds, i, "n_drain", 1, 0);
            im3Dbm[i] = TwoToneMeasurements.PoutDbm(ds, i, "n_drain", 2, -1);
            output.WriteLine($"  Pavl={sweepVals[i],5:F0} dBm  carrier={carDbm[i],8:F2}  IM3={im3Dbm[i],8:F2} dBm");
        }

        double carrierSlope = Slope(sweepVals, carDbm);
        double im3Slope     = Slope(sweepVals, im3Dbm);
        output.WriteLine($"  carrier slope = {carrierSlope:F2} (expect ~1)   IM3 slope = {im3Slope:F2} (expect ~3)");

        Assert.InRange(carrierSlope, 0.9, 1.1);   // fundamental rises 1:1
        Assert.InRange(im3Slope,     2.6, 3.4);   // third-order rises 3:1
    }

    // Least-squares slope of y vs x.
    private static double Slope(double[] x, double[] y)
    {
        int n = x.Length;
        double mx = x.Average(), my = y.Average();
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) { num += (x[i] - mx) * (y[i] - my); den += (x[i] - mx) * (x[i] - mx); }
        return num / den;
    }

    // ── 3. Unequal-amplitude guard on V_nTone ────────────────────────────────

    [Fact]
    public void Hero5_UnequalAmplitudes_CarriersScaleWithDrive()
    {
        // ToneRatio = 0.5 in hero5_unequal.cnl (V[2] = 0.5·V[1]). At low drive (linear regime), both
        // carriers see the same fundamental-band impedances, so the output carrier ratio must equal
        // the drive ratio — proving each tone is stamped at its own magnitude.
        var ds = RunHero5("hero5_unequal.cnl", start: -20, stop: -20, step: 1);
        Assert.True((double)ds["Converged"][0] > 0.5,
            "unequal-amplitude point did not converge.");

        Complex c1 = TwoToneMeasurements.Tone(ds, 0, "n_drain", 1, 0);
        Complex c2 = TwoToneMeasurements.Tone(ds, 0, "n_drain", 0, 1);
        double ratio = c2.Magnitude / c1.Magnitude;

        output.WriteLine($"|carrier(0,1)| / |carrier(1,0)| = {ratio:F4} (expect ≈ 0.5)");
        Assert.Equal(0.5, ratio, 0.03);   // within 3% (slight nonlinearity at -34 dBm is negligible)
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DataSet RunHero5(string cnl, double start, double stop, double step, double? tol = null)
    {
        var dir       = Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, cnl));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals)
                        with { SweepStart = start, SweepStop = stop, SweepStep = step };
        if (tol is not null) p = p with { Tol = tol.Value };
        var sw = new ParametricSweepAnalysis("SW_auto", p.SweepVarName!, p.SweepValues().ToArray(), hba.Name);
        return ParametricSweepEngine.Run(sw, lib, tb);
    }

    private static int NodeIdx(DataSet ds, string name)
        => Array.FindIndex(ds["V"].Axes[1].Labels!, s => s.Contains(name, StringComparison.OrdinalIgnoreCase));

    private record GoldenRow(int K1, int K2, double FreqHz, double Pave, double Re, double Im);

    private static List<GoldenRow> LoadGolden(string path)
    {
        var rows = new List<GoldenRow>();
        var ci = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.StartsWith("k1")) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(';');
            if (p.Length < 6) continue;
            rows.Add(new GoldenRow(
                int.Parse(p[0].Trim(), ci), int.Parse(p[1].Trim(), ci),
                double.Parse(p[2].Trim(), ci), double.Parse(p[3].Trim(), ci),
                double.Parse(p[4].Trim(), ci), double.Parse(p[5].Trim(), ci)));
        }
        return rows;
    }
}
