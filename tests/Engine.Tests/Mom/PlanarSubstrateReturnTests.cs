// SI-LOW — the series term a conducting substrate adds to a line, against the independent 2D solve in
// testdata/em-si-sigma, and when it is supplied. The kernel against the 3D reference is in
// ConductingDielectricReferenceTests.

using System.Globalization;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarSubstrateReturnTests
{
    private const double HSi = 100e-6, HOx = 2e-6, L = 1e-3;

    private static string Csv(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "testdata", "em-si-sigma", name);
            if (File.Exists(c)) return c;
        }
        throw new FileNotFoundException(name);
    }

    private static double[][] Rows(string name) =>
        [.. File.ReadLines(Csv(name)).Skip(1)
                .Select(l => l.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray())];

    private static (PlanarProblem Problem, PlanarPortResolution Port) Line(double widthM, double sigma)
    {
        double h = HSi + HOx;
        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(HSi, new EmMaterial(11.9) { SigmaSm = sigma }), new MediumLayer(HOx, new EmMaterial(3.9))],
            Termination.Air);
        var slab = new GroundedSlab(h, new EmMaterial(h / (HSi / 11.9 + HOx / 3.9)));
        var strip = new PlanarPolygon([new EmPoint(0, -widthM / 2), new EmPoint(L, -widthM / 2),
                                       new EmPoint(L, widthM / 2), new EmPoint(0, widthM / 2)]);
        var problem = new PlanarProblem([new PlanarConductorLayer("M", [strip], 5.8e7, 5e-6, h)], slab, 40e9, null, stack);
        var mesh = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default).Mesh;
        var port = PlanarPorts.ResolveAll(mesh, [new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, 50)])[0];
        return (problem, port);
    }

    /// <summary>R within 1.5 % and ΔL within 3 % of a finite-element solve of the cross-section, at three widths.</summary>
    [Fact]
    public void TheTermMatchesAnIndependentFiniteElementSolve()
    {
        foreach (var r in Rows("series-term.csv"))
        {
            double widthM = r[0] * 1e-6, sigma = r[1], f = r[2] * 1e9;
            var (problem, port) = Line(widthM, sigma);
            var z = PlanarSubstrateReturn.TryFor(problem, port, perfectStrip: true)!.SeriesPerMetreAt(f);
            string at = $"{r[0]} um, {sigma} S/m, {r[2]} GHz";
            Assert.True(Math.Abs(z.Real / r[3] - 1) < 0.015, $"{at}: R {z.Real:G5} against {r[3]:G5}");
            double dL = z.Imaginary / (2 * Math.PI * f) * 1e9;
            Assert.True(Math.Abs(dL / r[4] - 1) < 0.03, $"{at}: dL {dL:G4} against {r[4]:G4}");
        }
    }

    /// <summary>Gate 1 by construction: at 10 Ω·cm and above no calibration holds one, so nothing reads it.</summary>
    [Fact]
    public void NoTermIsSuppliedAtTenOhmCmOrAbove()
    {
        Assert.Null(TryAt(10.0));
        Assert.Null(TryAt(0.1));
        Assert.NotNull(TryAt(10.5));

        static PlanarSubstrateReturn? TryAt(double sigma)
        {
            var (problem, port) = Line(90e-6, sigma);
            return PlanarSubstrateReturn.TryFor(problem, port, perfectStrip: false);
        }
    }

    /// <summary>A coplanar pair carries a second current this term has no place for, so it declines.</summary>
    [Fact]
    public void AStandardThatIsNotOneStripDeclines()
    {
        var (problem, port) = Line(90e-6, 1e4);
        Assert.NotNull(PlanarSubstrateReturn.TryFor(problem, port, perfectStrip: false));
        Assert.Null(PlanarSubstrateReturn.TryFor(problem, port with { Reference = PlanarPortReference.CoplanarGround }, perfectStrip: false));
    }

    /// <summary>Gate 5, with the term and without it. The term retires the old range; a layer that has not shielded the
    /// line by the crossover gets the smaller one, and one that has gets nothing.</summary>
    [Fact]
    public void TheWarningNarrowsToWhatIsLeft()
    {
        Assert.Contains("1 GHz to 27 GHz", Warnings(1e4, supplied: false).Single());
        Assert.Empty(Warnings(1e4, supplied: true));
        Assert.Contains("9 GHz to 27 GHz", Warnings(1000, supplied: true).Single());

        static List<string> Warnings(double sigma, bool supplied)
        {
            var stack = new LayerStack(Termination.Pec,
                [new MediumLayer(HSi, new EmMaterial(11.9) { SigmaSm = sigma }), new MediumLayer(HOx, new EmMaterial(3.9))],
                Termination.Air);
            return [.. PlanarConductingMedium.Findings(stack, 1e9, 40e9, 27e9, false, supplied)
                                             .Where(f => f.Severity == EmSeverity.Warning).Select(f => f.Text)];
        }
    }

    /// <summary>What sizes a separation and picks a branch, with no mesh or earlier point: within 4 % of the 3D β.</summary>
    [Fact]
    public void TheEstimatedBetaIsCloseToTheReference()
    {
        foreach (var r in Rows("reference.csv").Where(r => r[0] > PlanarSubstrateReturn.MinConductivitySm))
        {
            var (problem, port) = Line(90e-6, r[0]);
            double beta = PlanarSubstrateReturn.TryFor(problem, port, perfectStrip: false)!.EstimatedBetaAt(r[1] * 1e9);
            Assert.True(Math.Abs(beta / r[3] - 1) < 0.04, $"{r[0]} S/m, {r[1]} GHz: {beta:G5} against {r[3]:G5}");
        }
    }
}
