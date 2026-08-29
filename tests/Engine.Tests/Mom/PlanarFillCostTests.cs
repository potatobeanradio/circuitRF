// L8c — Tier 8: what the fill actually costs, and R-fil-8's image-depth measurement.
//
// EVERYTHING HERE IS Category=Benchmark. These are reporting sweeps, not gates: they measure the
// numbers L8d and L8e are scheduled against, and following L8a's precedent a phase's own reporting
// sweep has no business spending Hero1BTests' wall-clock headroom.
//
// The brief asks for the breakdown, not just a total — "say what dominates: the smooth quadrature,
// the singular cores, or the LU" — so the timings are split into the four things that scale
// differently:
//
//   Dcim.Fit      per frequency, independent of N.        (L8a's R-lgf-5: the kernel IS per-frequency)
//   cores         ONCE per mesh, O(N²).                   (D6 — this is the thing the counter guards)
//   fill          per frequency, O(N²): the smooth remainder plus the assembly.
//   LU            per frequency, O(N³).
//
// Which of those dominates changes with N, and that crossover is the useful part of the answer.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarFillCostTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>§10.7's own worked example: 50 Ω microstrip on 1.6 mm FR-4, W ≈ 2.9 mm, 20 mm long.
    /// L8b measures N = 552 for it, and that is what this slice is scheduled against.</summary>
    private static PlanarProblem Hero(double fHz = 10e9) =>
        new([new PlanarConductorLayer("Metal",
                [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(20e-3, 0),
                                    new EmPoint(20e-3, 2.9e-3), new EmPoint(0, 2.9e-3)])],
                5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, fHz);

    /// <summary>A uniform n×m grid of the hero's own footprint, used to hit a chosen N exactly.
    /// N = (n−1)m + n(m−1).</summary>
    private static PlanarMesh UniformHero(int nx, int ny)
    {
        var gx = new double[nx + 1];
        var gy = new double[ny + 1];
        for (int i = 0; i <= nx; i++) gx[i] = 20e-3 * i / nx;
        for (int j = 0; j <= ny; j++) gy[j] = 2.9e-3 * j / ny;
        return PlanarFillTests.Grid(gx, gy);
    }

    private sealed record Cost(int N, int Cells, double KernelMs, double CoreMs, double FillMs,
                               double LuMs, long CoreBytes, long MatrixBytes, long AllocatedBytes);

    private Cost Measure(PlanarMesh mesh, string label, bool factor = true)
    {
        var slab   = GroundedSlab.Fr4Starter;
        var greens = new SpectralGreens(slab, 10e9);
        var st     = PlanarFillSettings.Default;

        long before = GC.GetTotalAllocatedBytes(precise: false);

        var sw = Stopwatch.StartNew();
        var dcimA = Dcim.Fit(greens, GreensKernel.VectorPotential);
        var dcimQ = Dcim.Fit(greens, GreensKernel.ScalarPotential);
        double kernelMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var cores = PlanarFill.BuildCores(mesh, st);
        double coreMs = sw.Elapsed.TotalMilliseconds;

        var termsA = PlanarKernelTerms.FromDcim(dcimA, st.Order, cores.RhoFloorM);
        var termsQ = PlanarKernelTerms.FromDcim(dcimQ, st.Order, cores.RhoFloorM);

        sw.Restart();
        var system = PlanarSystem.Build(cores, termsA, termsQ, 2.0 * Math.PI * 10e9);
        double fillMs = sw.Elapsed.TotalMilliseconds;

        double luMs = 0;
        if (factor)
        {
            sw.Restart();
            _ = system.Lu;
            luMs = sw.Elapsed.TotalMilliseconds;
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: false) - before;

        var c = new Cost(cores.UnknownCount, cores.CellCount, kernelMs, coreMs, fillMs, luMs,
                         cores.CoreBytes, PlanarSystem.MatrixBytes(cores.UnknownCount), allocated);

        _out.WriteLine($"── {label}: N = {c.N:N0} ({c.Cells:N0} cells), " +
                       $"{cores.ScalarPairs:N0} cell pairs + {cores.VectorPairs:N0} basis pairs");
        _out.WriteLine($"   Dcim.Fit (both kernels, per frequency) : {c.KernelMs,9:F0} ms");
        _out.WriteLine($"   geometric cores (ONCE per mesh)        : {c.CoreMs,9:F0} ms");
        _out.WriteLine($"   fill  (per frequency)                  : {c.FillMs,9:F0} ms");
        _out.WriteLine($"   LU    (per frequency)                  : {c.LuMs,9:F0} ms");
        _out.WriteLine($"   matrix {c.MatrixBytes / (1024.0 * 1024.0),8:F1} MB   cores {c.CoreBytes / (1024.0 * 1024.0),8:F1} MB" +
                       $"   total allocated {c.AllocatedBytes / (1024.0 * 1024.0),8:F0} MB");

        double perPoint = c.KernelMs + c.FillMs + c.LuMs;
        _out.WriteLine($"   ⇒ per frequency {perPoint / 1000.0,7:F2} s;  101-point sweep " +
                       $"{(c.CoreMs + 101 * perPoint) / 1000.0,8:F1} s");
        _out.WriteLine($"   ⇒ dominant term: {Dominant(c)}");
        return c;
    }

    private static string Dominant(Cost c)
    {
        var terms = new (string Name, double Ms)[]
        {
            ("Dcim.Fit (kernel, per frequency)", c.KernelMs),
            ("the singular cores (once)",        c.CoreMs),
            ("the smooth remainder (per freq)",  c.FillMs),
            ("the LU (per freq)",                c.LuMs),
        };
        var best = terms[0];
        foreach (var t in terms) if (t.Ms > best.Ms) best = t;
        return $"{best.Name} at {best.Ms:F0} ms";
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The three sizes the brief names
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T8_1_TheHero_N552()
    {
        var report = SurfaceMesher.Mesh(Hero());
        Assert.Equal(552, report.UnknownCount);         // L8b's own number, pinned here too
        var c = Measure(report.Mesh, "§10.7's FR-4 hero, meshed by SurfaceMesher");

        // §10.7 calls this size "Instant. The microstrip hero lives here." A 101-point sweep has to
        // be tolerable or L8d has nothing to build on.
        Assert.True(c.CoreMs + 101 * (c.KernelMs + c.FillMs + c.LuMs) < 300_000,
            "a 101-point sweep of the hero takes over five minutes — that is a finding, not a pass");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T8_2_TheInteractiveSize_N2000()
    {
        // §10.7: "2,000 → 64 MB. Interactive: seconds per frequency."
        var mesh = UniformHero(46, 22);              // (45)(22) + (46)(21) = 1956
        Assert.InRange(mesh.Bases.Count, 1800, 2200);
        Measure(mesh, "the interactive size");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T8_3_TheR17Ceiling_N5000()
    {
        // §10.7: "5,000 → 400 MB. The practical ceiling for lightweight."
        var mesh = UniformHero(72, 35);             // (71)(35) + (72)(34) = 4933
        Assert.InRange(mesh.Bases.Count, 4700, SurfaceMesher.UnknownCeiling);
        var c = Measure(mesh, "the R17 ceiling");

        // §10.7's own table says 400 MB at 5,000. The point of reporting the CORES beside it is that
        // D6's reuse is not free: it is an extra allocation that §10.7's table does not account for.
        _out.WriteLine($"   §10.7 predicts {PlanarSystem.MatrixBytes(5000) / (1024.0 * 1024.0):F0} MB at N = 5,000; " +
                       $"D6's cached cores add {c.CoreBytes * 100.0 / c.MatrixBytes:F0}% on top of the matrix.");
        _out.WriteLine("   …and the LU adds two more matrices beside them, which is why P1 " +
                       "(2026-08-29) re-pointed the refusals at PlanarSystem.ResidentBytes: " +
                       $"{PlanarSystem.ResidentBytes(5000) / (1024.0 * 1024.0):N0} MB resident at the " +
                       "peak of one frequency point, against the 400 MB §10.7's table quotes.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-fil-8 — the smallest fitted image depth, measured rather than assumed
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T8_4_TheSmallestFittedImageDepthRelativeToTheSmallestCell()
    {
        // D3's third bullet — "the images are smooth PROVIDED no fitted image depth is small compared
        // with a cell" — is a CONDITION, not a fact. This is the measurement that says whether it
        // holds, on both starters across the band.
        _out.WriteLine("substrate      f (GHz)   min|b| (µm)   smallest cell (µm)   ratio   images  surface waves");
        foreach (var (name, slab, cell) in new[]
                 {
                     ("FR-4 1.6 mm",  GroundedSlab.Fr4Starter,  84.5e-6),
                     ("GaAs 100 µm",  GroundedSlab.GaAsStarter,  2.16e-6),
                 })
            foreach (double f in new[] { 2e9, 10e9, 20e9 })
            {
                var greens = new SpectralGreens(slab, f);
                double worst = double.PositiveInfinity;
                int images = 0, waves = 0;
                foreach (var kernel in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
                {
                    var terms = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, kernel));
                    worst = Math.Min(worst, terms.SmallestImageDepth);
                    var model = Dcim.Fit(greens, kernel);
                    images = Math.Max(images, model.Images.Count);
                    waves = Math.Max(waves, model.SurfaceWaves.Count);
                }
                _out.WriteLine($"{name,-14} {f / 1e9,6:F0}   {worst * 1e6,11:F3}   {cell * 1e6,18:F2}   " +
                               $"{worst / cell,6:F3}   {images,6}   {waves,13}");
            }

        _out.WriteLine("");
        _out.WriteLine("A ratio below 1 means a fitted image sits CLOSER to the metal plane than a cell is wide,");
        _out.WriteLine("so its own 1/√(ρ²+b²) is nearly singular across that cell and the smooth-remainder");
        _out.WriteLine("quadrature has to know. This is why RemainderNodesNear is 8 rather than 3.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-fil-5 — the rule, reported rather than hidden
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T8_5_TheQuadratureRuleIsReported()
    {
        var st = PlanarFillSettings.Default;
        var report = SurfaceMesher.Mesh(Hero());
        var cores = PlanarFill.BuildCores(report.Mesh, st);

        _out.WriteLine("The shipped rule, keyed on τ = centroid separation ÷ larger cell diagonal:");
        _out.WriteLine($"  τ = 0     (self)  : {st.NearNodes}-point Gauss per axis on {st.SelfPanels}×{st.SelfPanels} " +
                       "Chebyshev-clustered panels, inner integral CLOSED FORM");
        _out.WriteLine($"  τ < {st.NearRatio}  (near)  : {st.NearNodes}-point on {st.TouchPanels}×{st.TouchPanels} panels, inner closed form");
        _out.WriteLine($"  τ < {st.FarRatio}  (mid)   : {st.MidNodes}-point, 1 panel");
        _out.WriteLine($"  τ ≥ {st.FarRatio}  (far)   : {st.FarNodes}-point, 1 panel");
        _out.WriteLine($"  smooth remainder  : {st.RemainderNodesNear}/{st.RemainderNodesMid}/{st.RemainderNodesFar}" +
                       " points per axis (near/mid/far), BOTH inner and outer");
        _out.WriteLine($"  extraction order  : {st.Order} (1/ρ, ln ρ and the constant)");
        _out.WriteLine($"  radial table      : spacing = {st.TableCellFraction} × smallest cell " +
                       $"= {st.TableCellFraction * cores.MinCellEdgeM * 1e6:F2} µm on the hero");
        _out.WriteLine($"  remainder ρ floor : {st.RhoFloorFraction} × smallest cell = {cores.RhoFloorM:E2} m");
        _out.WriteLine("");
        _out.WriteLine("Measured worst-case accuracy of that rule, against the independent correlation oracle at");
        _out.WriteLine("εᵣ = 1 (where the kernel is exact and only the quadrature can be wrong): 5.0e-6 relative.");
        _out.WriteLine("Against the Sommerfeld oracle with the real DCIM kernel, worst over both starters and");
        _out.WriteLine("2/10/20 GHz: 5.4e-3 — i.e. the KERNEL's own error (L8a: ≤ 6e-3), three decades above the");
        _out.WriteLine("fill's. Chasing the quadrature further would be wasted work, and saying so is the point.");
    }
}
