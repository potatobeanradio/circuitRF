// What the calibration standards cost, before and after solving only the two that are read.
//
// Category=Benchmark: this is a measurement, not a pass/fail gate. It exists because §0 of
// brief-em-sweep-performance put the standards at ~75% of a real user's de-embedded run and nothing
// in the repository measured WHY, or what the split between "necessary" and "discarded" was.
//
// The fixture is §0's own shape rather than the FR-4 hero, and that matters: the standards only
// dominate when a port is WIDE, because a standard reproduces the DUT's transverse gridlines across
// the port verbatim (D4 — the error box has to be the same object, so this is not negotiable). On a
// uniform 50 Ω line both standards are a handful of cells across and cost nothing. On a taper down to
// 12 Ω the wide port is ~20 cells across, and its longest standard alone can exceed the DUT's own
// unknown count.

using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class CalibrationStandardCostTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // §0's own stack: RO4350B 20 mil.
    private static readonly GroundedSlab Ro4350 = new(0.508e-3, new EmMaterial(3.66, 0.0037));

    private const double FLo = 1e9, FHi = 20e9;

    private static PlanarProblem Taper(double wNarrow, double wWide, double lengthM, double fHz)
    {
        var poly = new PlanarPolygon(
        [
            new EmPoint(0,       -0.5 * wNarrow),
            new EmPoint(lengthM, -0.5 * wWide),
            new EmPoint(lengthM,  0.5 * wWide),
            new EmPoint(0,        0.5 * wNarrow),
        ]);
        return new PlanarProblem([new PlanarConductorLayer("Metal", [poly], 5.8e7, 35e-6)], Ro4350, fHz);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheStandardsCost_BeforeAndAfterSolvingOnlyWhatIsRead()
    {
        var problem = Taper(1.10e-3, 6.71e-3, 50.8e-3, FHi);
        var meshed  = SurfaceMesher.Mesh(problem,
                          PlanarMeshSettings.Default with { CellsPerWavelength = 5 });
        var mesh    = meshed.Mesh;

        var (x0, y0, x1, y1) = problem.Bounds();
        double yc = 0.5 * (y0 + y1);
        var ports = PlanarPorts.ResolveAll(mesh,
        [
            new PlanarPort(1, new EmPoint(x0, yc), PlanarPortSide.MinX, 50.0),
            new PlanarPort(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, 12.0),
        ]);

        _out.WriteLine($"DUT N = {mesh.Bases.Count} ({Environment.ProcessorCount} cores)");
        foreach (var p in ports)
        {
            var set = PlanarCalibration.BuildSet(p, Ro4350, FLo, FHi);
            _out.WriteLine($"  port {p.Number}: {p.TransverseLines.Count - 1} cell(s) across, " +
                           $"standards N = {string.Join(" / ", set.Select(s => s.Mesh.Bases.Count))}");
        }

        double Time(Action a)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            a();
            return sw.Elapsed.TotalSeconds;
        }

        // Every context ONCE, outside the timing, exactly as the driver builds them (the DUT's in
        // PlanarSolve, a calibrator's in its constructor). Timing a fresh context would charge the
        // frequency-independent geometric cores to whichever solve happened to build them — L8c
        // measures those at 62% of a single-frequency solve, so charging them to the DUT alone
        // would flatter the standards by exactly that much.
        var dutCtx  = new PlanarSolveContext(mesh, ports);
        var calSets = ports.Select(p => PlanarCalibration.BuildSet(p, Ro4350, FLo, FHi)).ToArray();
        var calCtxs = calSets.Select(set => set.Select(s => new PlanarSolveContext(s.Mesh, s.Ports))
                                             .ToArray()).ToArray();

        var warm = PlanarFrequencyKernel.FromPair(PlanarKernelPair.Fit(Ro4350, FHi));
        dutCtx.RawScatteringAt(warm, FHi);
        foreach (var ctxs in calCtxs) foreach (var c in ctxs) c.RawScatteringAt(warm, FHi);

        _out.WriteLine("");
        _out.WriteLine("            DUT     all std   used std   point: all → used");

        double sumAll = 0, sumUsed = 0;
        foreach (double f in new[] { FLo, 4.47e9, FHi })
        {
            var kern = PlanarFrequencyKernel.FromPair(PlanarKernelPair.Fit(Ro4350, f));
            double dut = Time(() => dutCtx.RawScatteringAt(kern, f));

            double all = 0, used = 0;
            for (int pi = 0; pi < ports.Count; pi++)
            {
                var set   = calSets[pi];
                var delta = set.Skip(1).Select(s => s.LengthM - set[0].LengthM).ToArray();
                int pick  = PlanarCalibration.SelectSeparation(
                                delta, PlanarCalibration.EstimateBeta(Ro4350, f));

                for (int i = 0; i < set.Length; i++)
                {
                    double t = Time(() => calCtxs[pi][i].RawScatteringAt(kern, f));
                    all += t;
                    if (i == 0 || i == pick + 1) used += t;
                }
            }

            sumAll  += dut + all;
            sumUsed += dut + used;

            _out.WriteLine($"{f / 1e9,5:F2} GHz {dut,7:F2} s {all,8:F2} s {used,9:F2} s   " +
                           $"{dut + all,6:F2} → {dut + used,5:F2} s   " +
                           $"({(dut + all) / (dut + used),4:F2}x)");
        }

        // The three samples are geometrically spaced, so their sum stands in for a log sweep's own
        // average per point — which is the number a user experiences, not any one frequency's.
        _out.WriteLine($"      band  {sumAll,29:F2} → {sumUsed,5:F2} s   " +
                       $"({sumAll / sumUsed,4:F2}x)");

        _out.WriteLine("");
        _out.WriteLine("'used' is the short line plus the ONE long line that frequency selects; the");
        _out.WriteLine("rest were filled and discarded before this change. The saving is smallest at");
        _out.WriteLine("the BOTTOM of the band, where the selected long line is the longest one.");
    }
}
