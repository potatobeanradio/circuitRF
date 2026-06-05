using System.Numerics;
using CircuitRF.Engine.Loadpull;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Phase 4b-2 unit tests for the Baylis steepest-ascent search engine.
///
/// Uses synthetic analytic criterion functions (no HB) so tests are fast and deterministic.
/// The analytic optimum is known exactly; we verify the engine finds it within ≤ 1.1 VSWR.
/// </summary>
public class PursuitEngineTests(ITestOutputHelper output)
{
    // ── 1. Quadratic criterion centred at a known Z ───────────────────────────
    //
    // C(Z) = 1 − (VSWR(Z, Z_opt) − 1)² / A²
    //
    // This peaks at Z_opt with C = 1 and falls off quadratically in VSWR space,
    // mimicking what a real loadpull landscape looks like near its optimum.
    // The engine should find Z within 1.1 VSWR of Z_opt.

    private static double QuadraticCriterion(Complex z, Complex zOpt, double width = 2.0)
    {
        double v = RfHelpers.VswrFromZ(z, zOpt) - 1.0;   // = 0 at optimum
        return 1.0 - (v * v) / (width * width);
    }

    [Fact]
    public void FindsKnownOptimum_QuadraticCriterion()
    {
        var zOpt   = new Complex(80, 10);     // the true optimum
        var zStart = new Complex(50, 0);      // standard 50 Ω start
        int queries = 0;

        var engine = new PursuitEngine
        {
            Dn = 1.05, DsInitial = 1.3, ConvergenceThreshold = 1.05, MaxAscentSteps = 60
        };

        var result = engine.Run(zStart, z =>
        {
            queries++;
            double c = QuadraticCriterion(z, zOpt);
            // Return null (unscorable) only for the unit-circle boundary (VSWR > 10).
            if (RfHelpers.VswrFromZ(z, new Complex(50, 0)) > 10.0) return null;
            return c;
        });

        output.WriteLine(
            $"Start={zStart}  Opt={zOpt}  Found={result.OptimumZ:F3}  " +
            $"C={result.OptimumValue:F4}  Queries={queries}  " +
            $"Converged={result.Converged}  Unscorable={result.UnscorableZ.Count}");

        Assert.True(result.Converged, $"Engine did not converge: {result.AbortReason}");

        double vswr = RfHelpers.VswrFromZ(result.OptimumZ, zOpt);
        output.WriteLine($"VSWR(found, true_opt) = {vswr:F4}  (target ≤ 1.1)");
        Assert.True(vswr <= 1.1,
            $"Reported optimum {result.OptimumZ:F3} is {vswr:F4} VSWR from true optimum {zOpt} — exceeds 1.1.");

        Assert.True(queries <= 60, $"Too many queries ({queries}); engine not efficient.");
    }

    // ── 2. High-reactance optimum (tests geometry in Im(Z) direction) ─────────

    [Fact]
    public void FindsKnownOptimum_ReactiveShift()
    {
        var zOpt   = new Complex(60, -30);    // capacitive optimum
        var zStart = new Complex(50, 0);
        int queries = 0;

        var engine = new PursuitEngine { Dn = 1.05, DsInitial = 1.3, MaxAscentSteps = 60 };
        var result = engine.Run(zStart, z =>
        {
            queries++;
            return QuadraticCriterion(z, zOpt, width: 2.5);
        });

        double vswr = RfHelpers.VswrFromZ(result.OptimumZ, zOpt);
        output.WriteLine($"Queries={queries}  VSWR={vswr:F4}  Found={result.OptimumZ:F3}");
        Assert.True(result.Converged);
        Assert.True(vswr <= 1.1, $"VSWR={vswr:F4} > 1.1");
    }

    // ── 3. Non-compressing start point → abort with clear message ─────────────

    [Fact]
    public void UnscorableStart_AbortsWithClearMessage()
    {
        var engine = new PursuitEngine();
        var result = engine.Run(new Complex(50, 0), _ => null);   // all unscorable

        Assert.False(result.Converged);
        Assert.NotNull(result.AbortReason);
        Assert.True(result.AbortReason!.Contains("unscorable") || result.AbortReason.Contains("Start point"),
            $"Abort message does not mention unscorable: '{result.AbortReason}'");
        output.WriteLine($"Abort reason: {result.AbortReason}");
    }

    // ── 4. VSWR-distance metric consistency ────────────────────────────────────
    //
    // B5 fix: the search now works in Γ-space.  The step dG = (vswr−1)/(vswr+1) is
    // exact at Γ=0 (Z=50Ω) and approximate for |Γ|>0 (error ≤ ~5% for |Γ|<0.5).
    // The test verifies the neighbours are within a generous 10% of the target VSWR —
    // the algorithm is adaptive and converges regardless of the exact step size.

    [Theory]
    [InlineData(50,  0,  1.05)]
    [InlineData(80,  10, 1.05)]
    [InlineData(10,  -5, 1.05)]
    [InlineData(100, 30, 1.30)]
    public void TangentNeighbours_AreAtCorrectVswr(double zR, double zI, double dn)
    {
        var engine = new PursuitEngine { Dn = dn };

        var z = new Complex(zR, zI);
        var queriedZ = new List<Complex>();
        engine.Run(z, qz =>
        {
            queriedZ.Add(qz);
            return 1.0;   // always scorable, flat criterion → converges quickly
        });

        // queriedZ[0] = start, queriedZ[1] and [2] are the Γ-plane neighbours.
        if (queriedZ.Count >= 3)
        {
            double v1 = RfHelpers.VswrFromZ(z, queriedZ[1]);
            double v2 = RfHelpers.VswrFromZ(z, queriedZ[2]);
            output.WriteLine($"Z={z}  N1 VSWR={v1:F4}  N2 VSWR={v2:F4}  target={dn}");
            // 10% tolerance: the Γ-space step approximation is exact at Z=50Ω and
            // ≤5% off for |Γ|<0.5; the search is adaptive so exact step size matters less.
            Assert.True(Math.Abs(v1 - dn) / dn < 0.10,
                $"N1 VSWR={v1:F4} deviates >10% from Dn={dn}");
            Assert.True(Math.Abs(v2 - dn) / dn < 0.10,
                $"N2 VSWR={v2:F4} deviates >10% from Dn={dn}");
        }
    }
}
