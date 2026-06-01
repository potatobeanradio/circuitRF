using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Hero 1 gate: 4-port RLC network with an embedded .s2p block.
/// Sweep 1–3 GHz at 50 MHz steps (deliberately off the 0.1 GHz .s2p grid
/// so interpolation is always exercised).
/// Gate: max|S_sim(i,j,f) − S_ref(i,j,f)| &lt; 1e-6 across all 16 S-params.
/// </summary>
public class Hero1Tests
{
    private static string Hero1Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero1");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero1 not found");
    }

    [Fact]
    public void Hero1_SParams_MatchGolden_Below1e6()
    {
        var dir      = Hero1Dir();
        var cnlPath  = Path.Combine(dir, "hero1.cnl");
        var refPath  = Path.Combine(dir, "hero1_golden_result.s4p");

        // ── Load and elaborate the circuit ───────────────────────────────
        var (lib, tb) = CnlReader.ReadFile(cnlPath);
        var netlist   = new Elaborator(lib).Elaborate(tb);

        // ── Frequency sweep: 1–3 GHz, 50 MHz steps (41 points, off-grid) ─
        var freqs = Enumerable.Range(0, 41)
            .Select(i => (1.0 + i * 0.05) * 1e9)
            .ToArray();

        // ── Run simulation ────────────────────────────────────────────────
        var simSnp = SParameterEngine.Run(netlist, freqs);

        // ── Load reference ────────────────────────────────────────────────
        var refSnpRaw = TouchstoneIO.ReadFile(refPath);
        // Interpolate reference to the same grid (it uses a different grid)
        var refSnp = RFNetwork.Interpolate(
            refSnpRaw, freqs,
            InterpolationMethod.Linear, InterpolationFormat.RealImag,
            MatrixType.S, OutOfRangePolicy.WarnClamp);

        // ── Compare ───────────────────────────────────────────────────────
        Assert.Equal(simSnp.Ports,          refSnp.Ports);
        Assert.Equal(simSnp.FrequencyCount, refSnp.FrequencyCount);

        int    N       = simSnp.Ports;
        double maxDiff = 0.0;
        (int fi, int row, int col) worstAt = default;

        for (int fi = 0; fi < freqs.Length; fi++)
        for (int r  = 0; r  < N; r++)
        for (int c  = 0; c  < N; c++)
        {
            double diff = (simSnp.Matrices[fi][r, c] - refSnp.Matrices[fi][r, c]).Magnitude;
            if (diff > maxDiff) { maxDiff = diff; worstAt = (fi, r, c); }
        }

        Assert.True(maxDiff < 1e-6,
            $"max|S_sim − S_ref| = {maxDiff:G4} at " +
            $"f={freqs[worstAt.fi]/1e9:F3} GHz, " +
            $"S[{worstAt.row+1},{worstAt.col+1}]  (gate requires < 1e-6)");
    }
}
