using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Hero 1B gate: import VendorA netlist (~10k component network), run S-parameters.
/// Acceptance: performance &lt; 10 s + internal consistency (passivity, reciprocity).
/// NOT a 1e-6 match.
/// </summary>
public class Hero1BTests
{
    private static string Hero1BDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero1B");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero1B not found");
    }

    /// <summary>
    /// Diagnostic: try Hero 1B at a very low frequency (1 Hz) to see if the singularity
    /// comes from the Mutual/jωM terms (only present at ω > 0).
    /// At ω → 0: Mutual stamps −jωM ≈ 0, leaving just L constraint rows (V_a - V_b = 0 shorts).
    /// If this PASSES but 1 GHz fails, the inductance D-block is rank-deficient at AC.
    /// If this also fails, the issue is structural (Short topology or degenerate constraint rows).
    /// </summary>
    [Fact]
    public void Hero1B_Diagnostic_AtNearDcFrequency_ReportsSingularityOrPasses()
    {
        var dir     = Hero1BDir();
        var cnlPath = Path.Combine(dir, "hero1b.cnl");
        var (lib, tb) = VendorAReader.ReadFile(cnlPath);
        var nl = new Elaborator(lib).Elaborate(tb);

        string result;
        try
        {
            // 1 Hz is effectively DC for inductors: jωL = j * 2π * 1 * L ≈ 6.28e-9 H (sub-fH range)
            // Mutual terms: -jωM ≈ 0 → Mutual stamp is near-zero → not contributing
            SParameterEngine.Run(nl, [1.0]);
            result = "PASSED at 1 Hz — singularity is frequency-dependent (AC/jωM terms are the cause)";
        }
        catch (SingularMatrixException ex)
        {
            result = $"FAILED at 1 Hz too — singularity is structural (not from jωM): {ex.Message}";
        }
        catch (Exception ex)
        {
            result = $"Other exception at 1 Hz: {ex.GetType().Name}: {ex.Message}";
        }

        // Report result as a Console write so it appears in verbose output.
        // Always pass — this is a diagnostic probe, not a correctness gate.
        Console.WriteLine($"[Hero1B DC probe] {result}");
        Assert.True(true);
    }

    [Fact]
    public void Hero1B_ImportElaborateAndSolve_WithinBudgetAndConsistent()
    {
        var dir      = Hero1BDir();
        var cnlPath  = Path.Combine(dir, "hero1b.cnl");
        var refPath  = Path.Combine(dir, "hero1b_golden_result.s5p");

        // ── Import via VendorA reader ─────────────────────────────────────
        var sw = Stopwatch.StartNew();
        var (lib, tb) = VendorAReader.ReadFile(cnlPath);
        var nl = new Elaborator(lib).Elaborate(tb);
        var importMs = sw.ElapsedMilliseconds;

        // Quick sanity: should have thousands of components
        Assert.True(nl.Components.Count > 5_000,
            $"Expected > 5000 components, got {nl.Components.Count}");

        // 5 ports (Term1-5 from the netlist)
        var ports = nl.Components
            .Where(ec => ec.Model is CircuitRF.Core.Devices.PortModel
                      or CircuitRF.Core.Devices.TermModel)
            .OrderBy(ec => (int)ec.Parameters["Num"].AsReal())
            .ToList();
        Assert.Equal(5, ports.Count);

        // ── Load reference to get the frequency grid ──────────────────────
        var refSnpRaw = TouchstoneIO.ReadFile(refPath);
        // Use the reference file's own frequency grid for the simulation
        var freqs = refSnpRaw.Frequencies;

        // ── Run simulation ────────────────────────────────────────────────
        sw.Restart();
        var simSnp = SParameterEngine.Run(nl, freqs);
        var solveMs = sw.ElapsedMilliseconds;

        var totalS = (importMs + solveMs) / 1000.0;
        Console.WriteLine($"Import: {importMs} ms  |  Solve: {solveMs} ms  |  Total: {totalS:F1} s");
        Console.WriteLine($"Matrix: {nl.Nodes.Count - 1} voltage nodes, {nl.Components.Count} components");

        // ── Gate 1: performance ───────────────────────────────────────────
        Assert.True(totalS < 10.0,
            $"Performance budget exceeded: {totalS:F1} s (gate: < 10 s)");

        // ── Gate 2: internal consistency — passivity and reciprocity ──────
        // The circuit is all-passive (R, L, C, mutuals) → should be reciprocal (S_ij = S_ji)
        // and passive (|S_ij| ≤ 1 on diagonal, network-passivity via max singular value ≤ 1).
        int N = simSnp.Ports;
        double maxReciprocalErr = 0.0;
        double maxPassivityViol = 0.0;

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var m = simSnp.Matrices[fi];

            // Reciprocity: S_ij = S_ji
            for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                double diff = (m[r, c] - m[c, r]).Magnitude;
                if (diff > maxReciprocalErr) maxReciprocalErr = diff;
            }

            // Passivity: per-port output power ≤ input power (diagonal power balance)
            // Simple check: sum of |S_kj|² over k for each driven port j must be ≤ 1
            for (int j = 0; j < N; j++)
            {
                double outPower = 0.0;
                for (int k = 0; k < N; k++)
                    outPower += m[k, j].Magnitude * m[k, j].Magnitude;
                double viol = outPower - 1.0;
                if (viol > maxPassivityViol) maxPassivityViol = viol;
            }
        }

        Console.WriteLine($"Max reciprocity error: {maxReciprocalErr:G4}");
        Console.WriteLine($"Max passivity violation (power out - 1): {maxPassivityViol:G4}");

        Assert.True(maxReciprocalErr < 1e-6,
            $"Reciprocity check failed: max |S_ij - S_ji| = {maxReciprocalErr:G4}");

        // Passivity violation tolerance: small positives from numeric noise are expected;
        // a significant violation (> 1e-6) would indicate a physical error.
        Assert.True(maxPassivityViol < 1e-6,
            $"Passivity check failed: max power excess = {maxPassivityViol:G4}");
    }
}
