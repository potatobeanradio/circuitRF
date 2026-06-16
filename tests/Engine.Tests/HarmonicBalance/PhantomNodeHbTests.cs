using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate test for the unit-token phantom-node fix (brief-unit-token-phantom-nodes).
///
/// Verifies that after the fix:
///   1. The V cube node axis contains no "dBm" or "V" phantom nodes.
///   2. Vout2 (a linear back-solved node) appears on the axis.
///   3. Vout2's fundamental is non-zero and ≈ Vout's fundamental
///      (C2 = 1 mF is a near-short at 2 GHz, so Vout2 ≈ Vout).
/// </summary>
public class PhantomNodeHbTests(ITestOutputHelper output)
{
    private static string PhantomNodesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "PhantomNodes");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/PhantomNodes not found");
    }

    [Fact]
    public void Hb_Vout2_NonZeroFundamental()
    {
        var dir = PhantomNodesDir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "phantom_nodes.cnl"));

        // Run via the parametric sweep (single point at Pin=-10 dBm)
        var sw = tb.Analyses.OfType<ParametricSweepAnalysis>().First();
        var ds = ParametricSweepEngine.Run(sw, lib, tb);

        // V cube axes: [Pin, node, harmonic]
        var vCube = ds["V"];
        Assert.True(vCube.Rank >= 3, $"V cube rank should be ≥ 3, got {vCube.Rank}");

        string[] nodeLabels = vCube.Axes[1].Labels!;

        output.WriteLine($"Node axis labels: {string.Join(", ", nodeLabels)}");

        // No phantom unit nodes
        Assert.DoesNotContain("dBm", nodeLabels);
        Assert.DoesNotContain("V",   nodeLabels);

        // Vout2 is present (linear back-solved node)
        int vout2Idx = Array.IndexOf(nodeLabels, "Vout2");
        Assert.True(vout2Idx >= 0, $"'Vout2' not found in V cube node axis. Labels: {string.Join(", ", nodeLabels)}");

        // Vout (drain) is present
        int voutIdx = Array.IndexOf(nodeLabels, "Vout");
        Assert.True(voutIdx >= 0, $"'Vout' not found in V cube node axis. Labels: {string.Join(", ", nodeLabels)}");

        // harmonic index 1 = fundamental
        var vout2Fund = (Complex)vCube[0, vout2Idx, 1];
        var voutFund  = (Complex)vCube[0, voutIdx,  1];

        output.WriteLine($"|Vout[f0]|  = {voutFund.Magnitude:G4} V");
        output.WriteLine($"|Vout2[f0]| = {vout2Fund.Magnitude:G4} V");

        // Vout2 fundamental must be non-zero (not a back-solve failure)
        Assert.True(vout2Fund.Magnitude > 0.01,
            $"Vout2 fundamental near-zero ({vout2Fund.Magnitude:G4} V) — back-solve may have failed");

        // C2 = 1 mF at 2 GHz: |Z_C2| = 1/(2π·2e9·1e-3) ≈ 8e-11 Ω ≪ R2=80 Ω
        // → |Vout2/Vout| ≈ 1.0 within 0.1%
        double voutMag  = voutFund.Magnitude;
        double vout2Mag = vout2Fund.Magnitude;
        if (voutMag > 1e-6)
        {
            double ratio = vout2Mag / voutMag;
            Assert.True(ratio > 0.999 && ratio < 1.001,
                $"|Vout2/Vout| = {ratio:G4} — C2=1mF at 2 GHz should make Vout2 ≈ Vout (expected ~1.000)");
        }
    }
}
