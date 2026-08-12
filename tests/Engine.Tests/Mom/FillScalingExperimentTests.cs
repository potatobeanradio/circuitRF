using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class FillScalingExperimentTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Fact]
    [Trait("Category", "Benchmark")]
    public void HowWellDoesOneFillScale()
    {
        const double f = 10e9;
        var slab    = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, f);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var k       = PlanarLineFixtures.Kernel(slab, f);
        double w    = 2.0 * Math.PI * f;

        double Time(int? cap)
        {
            var st    = PlanarFillSettings.Default with { MaxDegreeOfParallelism = cap };
            var cores = PlanarFill.BuildCores(mesh, st);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var z  = PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, w);
            sw.Stop();
            Assert.Equal(mesh.Bases.Count, z.RowCount);
            return sw.Elapsed.TotalSeconds;
        }

        Time(2);   // warm
        double t1 = Time(1);
        _out.WriteLine($"N = {mesh.Bases.Count}, {Environment.ProcessorCount} cores — ONE fill:");
        _out.WriteLine($"  cap  1 : {t1,6:F1} s   1.00x");
        foreach (int c in new[] { 2, 4, 6, 8, 10 })
        {
            double t = Time(c);
            _out.WriteLine($"  cap {c,2} : {t,6:F1} s   {t1 / t:4:F2}x  ({100.0 * (t1 / t) / c:F0}% efficiency)");
        }
        double tu = Time(null);
        _out.WriteLine($"  unbnd  : {tu,6:F1} s   {t1 / tu:F2}x");
    }
}
