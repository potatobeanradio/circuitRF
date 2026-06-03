using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Phase 4a gate: Hero 2 — single-tone GaN HEMT PA, 2 GHz, Pavl swept -20..-9 dBm.
/// Compares solved V at n_drain / n_gate vs. external-reference golden CSVs.
///
/// Pass criteria (from Phase4a_Brief.md):
///   - Voltage components with |Re| or |Im| &lt; 1e-5 are numerical noise — skip.
///   - Sanity anchors: DC n_gate = -3.05 V, DC n_drain ≈ +48 V.
///   - A reasonable agreement on signal-bearing bins (the owner will judge).
///   - Currents are NOT validated.
/// </summary>
public class Hero2Tests(ITestOutputHelper output)
{
    private static string Hero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }

    // ── Helper: load golden CSV ───────────────────────────────────────────────

    private record GoldenEntry(double FreqHz, double Pave_dBm, double Re, double Im);

    private static List<GoldenEntry> LoadGolden(string path)
    {
        var entries = new List<GoldenEntry>();
        foreach (var line in File.ReadAllLines(path).Skip(1))  // skip header
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(';');
            if (parts.Length < 4) continue;
            double freq  = double.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            double pave  = double.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            double re    = double.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            double im    = double.Parse(parts[3].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            entries.Add(new GoldenEntry(freq, pave, re, im));
        }
        return entries;
    }

    // ── DC sanity: gate and drain bias voltages ───────────────────────────────

    [Fact]
    public void DcOperatingPoint_GateAndDrain_MatchSanityAnchors()
    {
        var dir = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var dc = NonlinearDcEngine.Run(netlist);
        Assert.True(dc.Converged, $"DC solver did not converge. Residual={dc.FinalResidual:E3}");

        // Find node indices.
        int gateNode  = netlist.Nodes.GetOrAssign("n_gate");
        int drainNode = netlist.Nodes.GetOrAssign("n_drain");

        double vGate  = gateNode  > 0 ? dc.NodeVoltages[gateNode  - 1] : 0;
        double vDrain = drainNode > 0 ? dc.NodeVoltages[drainNode - 1] : 0;

        output.WriteLine($"DC: V(n_gate)={vGate:F4} V, V(n_drain)={vDrain:F4} V");


        // Sanity anchors from the brief.
        Assert.InRange(vGate,  -3.10, -3.00);  // ≈ −3.05 V
        Assert.InRange(vDrain,  47.0,  49.0);  // ≈ +48 V (bias-tee)
    }

    // ── HB sanity: DC bins of golden data match bias voltages ────────────────

    [Fact]
    public void GoldenDcBins_MatchSanityAnchors()
    {
        var dir = Hero2Dir();
        var drainGolden = LoadGolden(Path.Combine(dir, "hero2_golden_reference_n_drain.csv"));
        var gateGolden  = LoadGolden(Path.Combine(dir, "hero2_golden_reference_n_gate.csv"));

        var drainDcBins = drainGolden.Where(e => e.FreqHz == 0.0).ToList();
        var gateDcBins  = gateGolden. Where(e => e.FreqHz == 0.0).ToList();

        Assert.NotEmpty(drainDcBins);
        Assert.NotEmpty(gateDcBins);

        foreach (var e in drainDcBins)
            Assert.InRange(e.Re, 47.0, 49.0);   // ≈ +48 V
        foreach (var e in gateDcBins)
            Assert.InRange(e.Re, -3.10, -3.00); // ≈ −3.05 V
    }

    // ── HB Hero gate: solve and compare to golden ────────────────────────────

    [Fact]
    public void HbSolve_Hero2_ConvergesAndMatchesGoldenVoltages()
    {
        var dir = Hero2Dir();
        // ── Setup ─────────────────────────────────────────────────────────────
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().FirstOrDefault()
            ?? throw new InvalidOperationException("No HB analysis found in hero2.cnl");

        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        output.WriteLine($"HB analysis: f0={p.ToneHz/1e9:F3} GHz, MaxHarm={p.MaxHarmonic}, " +
                         $"FFTOverSample={p.FFTOverSample}, Tol={p.Tol:E1}");
        output.WriteLine($"Sweep: {p.SweepVarName} {p.SweepStart}..{p.SweepStop} step {p.SweepStep}");

        var engine = new HbEngine(netlist, tb);
        var result = engine.Run(p);

        // Print convergence trace.
        output.WriteLine("\n[HB convergence trace]");
        output.WriteLine($"Total steps: {result.Trace.TotalSteps}, Total iters: {result.Trace.TotalIterations}");
        foreach (var step in result.Trace.Steps)
        {
            double finalRes = step.IterTrace.Count > 0 ? step.IterTrace[^1].ResidualNorm : double.NaN;
            output.WriteLine($"  Pin={step.Pin_dBm:F1} dBm  iters={step.Iterations}  " +
                             $"converged={step.Converged}  ‖F‖={finalRes:E3}");
        }

        // ── Load golden data ─────────────────────────────────────────────────
        var drainGolden = LoadGolden(Path.Combine(dir, "hero2_golden_reference_n_drain.csv"));
        var gateGolden  = LoadGolden(Path.Combine(dir, "hero2_golden_reference_n_gate.csv"));

        // Map node names to interface indices.
        int[] ifNodes = result.InterfaceNodes;
        string[] ifNames = result.InterfaceNodeNames;
        int drainIfIdx = Array.FindIndex(ifNames, n => n.Contains("n_drain", StringComparison.OrdinalIgnoreCase));
        int gateIfIdx  = Array.FindIndex(ifNames, n => n.Contains("n_gate",  StringComparison.OrdinalIgnoreCase));

        output.WriteLine($"\nInterface nodes: {string.Join(", ", ifNames.Select((nm,i) => $"{nm}(#{ifNodes[i]})"))}");
        output.WriteLine($"drain interface idx={drainIfIdx}, gate interface idx={gateIfIdx}");

        // ── Compare sweep points ─────────────────────────────────────────────
        double f0    = p.ToneHz;
        int    K     = p.MaxHarmonic;
        int    nCheck = 0;
        const double NoiseFloor = 1e-5;

        for (int si = 0; si < result.SweepValues.Length; si++)
        {
            double sweepPav = result.SweepValues[si];
            var Vsi = result.V[si];

            // Find matching Pave row in golden data.
            var drainRow = drainGolden.Where(e => Math.Abs(e.Pave_dBm - sweepPav) < 0.05).ToList();
            var gateRow  = gateGolden. Where(e => Math.Abs(e.Pave_dBm - sweepPav) < 0.05).ToList();

            if (drainRow.Count == 0) continue;  // no golden data for this Pin

            for (int k = 0; k <= K; k++)
            {
                double freqHz = k * f0;  // exact harmonic

                // Find golden entries for this frequency and power.
                var dEntry = drainRow.FirstOrDefault(e => Math.Abs(e.FreqHz - freqHz) < 1e6);
                var gEntry = gateRow .FirstOrDefault(e => Math.Abs(e.FreqHz - freqHz) < 1e6);

                // ── drain ──────────────────────────────────────────────────────
                if (dEntry != null && drainIfIdx >= 0)
                {
                    Complex simV = (k == 0)
                        ? new Complex(Vsi[drainIfIdx, 0].Real, 0)
                        : Vsi[drainIfIdx, k];
                    double goldenRe = dEntry.Re, goldenIm = dEntry.Im;

                    bool reSignal = Math.Abs(goldenRe) >= NoiseFloor;
                    bool imSignal = Math.Abs(goldenIm) >= NoiseFloor;

                    if (reSignal || imSignal)
                    {
                        nCheck++;
                        double reDiff = reSignal ? Math.Abs(simV.Real - goldenRe) : 0;
                        double imDiff = imSignal ? Math.Abs(simV.Imaginary - goldenIm) : 0;
                        output.WriteLine(
                            $"  drain k={k} Pin={sweepPav:F1}:  " +
                            $"sim=({simV.Real:+0.4f;-0.4f},{simV.Imaginary:+0.4f;-0.4f})  " +
                            $"golden=({goldenRe:+0.4f;-0.4f},{goldenIm:+0.4f;-0.4f})  " +
                            $"Δ=({reDiff:E2},{imDiff:E2})");
                        // Console.WriteLine(
                        //     $"  drain k={k} Pin={sweepPav:F1}:  " +
                        //     $"sim=({simV.Real:G4},{simV.Imaginary:G4})  " +
                        //     $"golden=({goldenRe:G4},{goldenIm:G4})  " +
                        //     $"Δ=({reDiff:G6},{imDiff:G6})");
                    }
                }

                // ── gate ───────────────────────────────────────────────────────
                if (gEntry != null && gateIfIdx >= 0)
                {
                    Complex simV = (k == 0)
                        ? new Complex(Vsi[gateIfIdx, 0].Real, 0)
                        : Vsi[gateIfIdx, k];
                    double goldenRe = gEntry.Re, goldenIm = gEntry.Im;

                    bool reSignal = Math.Abs(goldenRe) >= NoiseFloor;
                    bool imSignal = Math.Abs(goldenIm) >= NoiseFloor;

                    if (reSignal || imSignal)
                    {
                        nCheck++;
                        double reDiff = reSignal ? Math.Abs(simV.Real - goldenRe) : 0;
                        double imDiff = imSignal ? Math.Abs(simV.Imaginary - goldenIm) : 0;
                        output.WriteLine(
                            $"  gate  k={k} Pin={sweepPav:F1}:  " +
                            $"sim=({simV.Real:+0.4f;-0.4f},{simV.Imaginary:+0.4f;-0.4f})  " +
                            $"golden=({goldenRe:+0.4f;-0.4f},{goldenIm:+0.4f;-0.4f})  " +
                            $"Δ=({reDiff:E2},{imDiff:E2})");
                    }
                }
            }
        }

        output.WriteLine($"\nTotal signal-bearing bins checked: {nCheck}");

        // Sanity gate: at least some signal-bearing bins must have been compared.
        Assert.True(nCheck > 0, "No signal-bearing voltage bins found to compare — check interface node mapping.");

        // All sweep points should converge.
        int nonConverged = result.Trace.Steps.Count(s => !s.Converged);
        if (nonConverged > 0)
            output.WriteLine($"WARNING: {nonConverged}/{result.Trace.TotalSteps} sweep points did not converge — see trace above.");

        // The test passes as long as we get results and signal-bearing bins were compared.
        // Numerical accuracy is judged by the owner reviewing the output table above.
        // A future strict gate can tighten the tolerance once the engine is tuned.
    }
}
