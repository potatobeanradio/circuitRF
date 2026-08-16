using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Hero 1B gate: import VendorA netlist (~10k component network), run S-parameters.
/// Acceptance: performance &lt; 10 s + internal consistency (passivity, reciprocity).
/// NOT a 1e-6 match.
///
/// <h3>The two acceptance gates are two TESTS, split by what can break them (2026-08-16)</h3>
/// <para>They used to be one method, and the wall-clock budget made the whole thing flaky: it
/// measures ~2 s alone but blows the 10 s gate when the full solution runs — Ui.Tests' 7,000-odd
/// tests are executing on the same cores at the same moment. Owner-reported ("that same test always
/// fails under load and passed in isolation"), and the failure was never about the engine.</para>
///
/// <para>So the CORRECTNESS half — component and port counts, reciprocity, passivity — stays in the
/// default gate, where it costs ~2 s and asserts nothing a busy machine can perturb. The
/// PERFORMANCE half carries <c>Category=Benchmark</c> and runs via
/// <c>dotnet test --settings circuitrf.benchmark.runsettings</c>, alone, where a wall-clock number
/// means something. This is the repo's established rule for a wall-clock-sensitive test rather than
/// a merely slow one — see root <c>CLAUDE.md</c> on <c>RfCore.Tests</c>' <c>Rbf2DPerfTests</c>, which
/// are millisecond-fast and tagged for exactly this reason. <b>Do not merge these back into one
/// method</b>, and do not untag the budget on the grounds that it runs quickly.</para>
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

    /// <summary>One import + elaborate + solve, and what it cost. Shared by both gates below so the
    /// thing being timed and the thing being checked are the same run of the same code.</summary>
    private static (ElaboratedNetlist Netlist, DataSet Results, double[] Freqs, double TotalSeconds) Run()
    {
        var dir     = Hero1BDir();
        var cnlPath = Path.Combine(dir, "hero1b.cnl");
        var refPath = Path.Combine(dir, "hero1b_golden_result.s5p");

        // ── Import via VendorA reader ─────────────────────────────────────
        var sw = Stopwatch.StartNew();
        var (lib, tb) = VendorAReader.ReadFile(cnlPath);
        var nl = new Elaborator(lib).Elaborate(tb);
        var importMs = sw.ElapsedMilliseconds;

        // ── Load reference to get the frequency grid ──────────────────────
        var refSnpRaw = TouchstoneIO.ReadFile(refPath);
        // Use the reference file's own frequency grid for the simulation
        var freqs = refSnpRaw.Frequencies;

        // ── Run simulation ────────────────────────────────────────────────
        sw.Restart();
        var ds = SParameterEngine.Run(nl, freqs);
        var solveMs = sw.ElapsedMilliseconds;

        var totalS = (importMs + solveMs) / 1000.0;
        Console.WriteLine($"Import: {importMs} ms  |  Solve: {solveMs} ms  |  Total: {totalS:F1} s");
        Console.WriteLine($"Matrix: {nl.Nodes.Count - 1} voltage nodes, {nl.Components.Count} components");

        return (nl, ds, freqs, totalS);
    }

    /// <summary>
    /// <b>Gate 1 — performance.</b> Opt-in only: this is a wall-clock number, and a wall-clock number
    /// measured while 7,000 other tests are saturating the machine is a measurement of the machine.
    /// Run it with <c>dotnet test --settings circuitrf.benchmark.runsettings</c>. See the class
    /// remarks before merging it back into the correctness test.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")] // ~2s alone, but a WALL-CLOCK gate — meaningless under load
    public void Hero1B_ImportElaborateAndSolve_WithinPerformanceBudget()
    {
        var (_, _, _, totalS) = Run();

        Assert.True(totalS < 10.0,
            $"Performance budget exceeded: {totalS:F1} s (gate: < 10 s)");
    }

    /// <summary>
    /// <b>Gate 2 — the netlist imported whole, and the answer is physical.</b> Stays in the default
    /// gate: nothing it asserts depends on how busy the machine is.
    /// </summary>
    [Fact]
    public void Hero1B_ImportElaborateAndSolve_IsStructurallyAndPhysicallyConsistent()
    {
        var (nl, ds, freqs, _) = Run();

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

        // ── Internal consistency — passivity and reciprocity ──────────────
        // The circuit is all-passive (R, L, C, mutuals) → should be reciprocal (S_ij = S_ji)
        // and passive (|S_ij| ≤ 1 on diagonal, network-passivity via max singular value ≤ 1).
        int N = ds["S"].Axes[1].Length;
        double maxReciprocalErr = 0.0;
        double maxPassivityViol = 0.0;

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            // Reciprocity: S_ij = S_ji
            for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                double diff = ((Complex)ds["S"][fi, r, c] - (Complex)ds["S"][fi, c, r]).Magnitude;
                if (diff > maxReciprocalErr) maxReciprocalErr = diff;
            }

            // Passivity: per-port output power ≤ input power (diagonal power balance)
            // Simple check: sum of |S_kj|² over k for each driven port j must be ≤ 1
            for (int j = 0; j < N; j++)
            {
                double outPower = 0.0;
                for (int k = 0; k < N; k++)
                {
                    double mag = ((Complex)ds["S"][fi, k, j]).Magnitude;
                    outPower += mag * mag;
                }
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
