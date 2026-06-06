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
/// Phase 4c Step 5 — the two-tone Newton solve loop (<see cref="HbNewton2D.Solve"/>).
///
/// Drives the Step-4 Jacobian blocks to a self-consistent two-tone steady state on the Hero-2 GaN
/// HEMT: the physical DC bias network (ExtractDC), a simple resistive source/load at the AC mixing
/// products, and a synthetic two-tone Norton drive at the two carrier bins (1,0)/(0,1). Confirms
///   (a) the Newton loop converges (‖F‖ → tol),
///   (b) the DC operating point is preserved (gate ≈ -3.05 V, drain ≈ 48 V),
///   (c) intermodulation products NOT directly driven (e.g. IM3 (2,-1)) are populated by the
///       device mixing — the qualitative proof the 2-D lattice solve actually mixes the tones.
/// </summary>
public class HbSolve2DTests(ITestOutputHelper output)
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

    [Fact]
    public void Solve2D_TwoToneSteadyState_ConvergesAndMixes()
    {
        var dir       = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2_convergence.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var extractor = new HbLinearExtractor(netlist, AnalysisSettings.Default);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        var nodeNames = ifNodes.Select(n => netlist.Nodes.NameOf(n)).ToArray();
        int gate  = Array.FindIndex(nodeNames, s => s.Contains("gate",  StringComparison.OrdinalIgnoreCase));
        int drain = Array.FindIndex(nodeNames, s => s.Contains("drain", StringComparison.OrdinalIgnoreCase));
        Assert.True(gate >= 0 && drain >= 0);

        const int order = 3;
        var grid     = new MixingGrid(order);
        var (N1, N2) = HbFft2D.GridSizes(order, order, 2);
        int M        = grid.MixCount;
        double f1 = 1.995e9, f2 = 2.005e9;

        // ── Linear interface: physical DC bias (ExtractDC); resistive 25Ω source / 50Ω load at AC. ──
        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int m = 1; m < M; m++)
        {
            var y = new Complex[N, N];
            y[gate,  gate]  = new Complex(0.04, 0);   // 25 Ω source
            y[drain, drain] = new Complex(0.02, 0);   // 50 Ω load
            yNN[m]  = y;
            iSrc[m] = new Complex[N];
        }
        // Two-tone Norton drive at the carriers — deliberately unequal to exercise asymmetry.
        iSrc[grid.IndexOf(1, 0)][gate] = new Complex(0.010, 0);
        iSrc[grid.IndexOf(0, 1)][gate] = new Complex(0.008, 0);

        // ── DC seed from the Phase-3 nonlinear-DC engine; small AC seed. ──
        var dc = NonlinearDcEngine.Run(netlist, AnalysisSettings.Default);
        var V  = new Complex[N, M];
        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            double vdc = circNode > 0 && circNode - 1 < dc.NodeVoltages.Length
                ? dc.NodeVoltages[circNode - 1] : 0.0;
            V[n, 0] = new Complex(vdc, 0);
            for (int m = 1; m < M; m++) V[n, m] = new Complex(1e-3, 1e-3);
        }

        // ── Solve ──────────────────────────────────────────────────────────────
        const double tol = 1e-7;
        var res = HbNewton2D.Solve(V, yNN, iSrc, grid, f1, f2, N, N1, N2,
            netlist, ifNodes, AnalysisSettings.Default, tol);

        output.WriteLine($"Two-tone solve: N={N}, M={M}, grid {N1}×{N2}, order {order}");
        output.WriteLine($"  converged={res.Converged} in {res.Iterations} iters");
        output.WriteLine("  ‖F‖ trajectory: " +
            string.Join(" → ", res.IterTrace.Select(r => r.ResidualNorm.ToString("E2"))));
        output.WriteLine($"  DC: {nodeNames[gate]}={V[gate,0].Real:F3} V, {nodeNames[drain]}={V[drain,0].Real:F3} V");
        int c10 = grid.IndexOf(1, 0), c01 = grid.IndexOf(0, 1);
        int im3 = grid.IndexOf(2, -1), im2 = grid.IndexOf(1, -1);
        output.WriteLine($"  carriers : drain(1,0)={V[drain,c10].Magnitude:E3}  drain(0,1)={V[drain,c01].Magnitude:E3}");
        output.WriteLine($"  IM2 (1,-1): drain={V[drain,im2].Magnitude:E3}   IM3 (2,-1): drain={V[drain,im3].Magnitude:E3}");

        Assert.True(res.Converged,
            $"Two-tone Newton did not converge: ‖F‖={res.IterTrace.LastOrDefault()?.ResidualNorm:E3} " +
            $"after {res.Iterations} iters.");

        // DC operating point preserved by the self-consistent k=0 solve.
        Assert.Equal(-3.05, V[gate, 0].Real, 0.5);
        Assert.True(V[drain, 0].Real > 40.0, $"drain DC = {V[drain,0].Real:F2} V, expected ≈48 V");

        // Carriers developed from the drive.
        Assert.True(V[drain, c10].Magnitude > 1e-4, "carrier (1,0) not developed");
        Assert.True(V[drain, c01].Magnitude > 1e-4, "carrier (0,1) not developed");

        // IM3 (2,-1) is NOT driven directly — its presence proves the device mixed the tones.
        Assert.True(res.INl[drain, im3].Magnitude > 1e-9,
            $"IM3 (2,-1) mixing product absent (|INl|={res.INl[drain,im3].Magnitude:E3}); two-tone mixing failed.");
    }
}
