// Owner, 2026-08-09: "I've seen geometry in commercial MoM take 2 min to mesh (or longer). It depends
// on geometry." Measured on this repo's own single-polygon line fixture the mesher is 0.1-0.4 ms — but
// that fixture is ONE polygon, and the mesher's dominant term is layers x grid rows x POLYGONS in the
// span scan. R17's ceiling bounds the CELL count, not the polygon count, so the measurement does not
// cover the case the owner is describing. These gates are about the reporting being real, not about
// how long it happens to take on a line.

using System;
using System.Collections.Generic;
using System.Threading;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public class SurfaceMesherProgressTests
{
    private sealed class Collector : IProgress<RunProgress>
    {
        public readonly List<RunProgress> Seen = [];
        public void Report(RunProgress v) => Seen.Add(v);
    }

    private static PlanarProblem Fixture() => PlanarLineFixtures.Fr4Line(20e-3, 10e9);

    [Fact]
    public void Meshing_ReportsNamedStages_AndAScanBarWithARealDenominator()
    {
        var collector = new Collector();
        var control = new RunControl { Progress = collector };

        var report = SurfaceMesher.Mesh(Fixture(), PlanarMeshSettings.Default,
                                        PlanarEdgeReference.ConductorWidth, control);

        Assert.True(report.CanSolve);
        Assert.Contains(collector.Seen, p => p.Stage.Contains("measuring", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(collector.Seen, p => p.Stage.Contains("grid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(collector.Seen, p => p.StageTotal > 0 && p.StageCompleted > 0);
    }

    [Fact]
    public void TheMesher_NeverTicksTheOuterCounter()
    {
        // Load-bearing: the mesher also runs INSIDE a sweep, where the outer counter means frequency
        // points. Ticking it here would count meshing as points solved.
        var control = new RunControl { Total = 101, Progress = new Collector() };

        SurfaceMesher.Mesh(Fixture(), PlanarMeshSettings.Default,
                           PlanarEdgeReference.ConductorWidth, control);

        Assert.Equal(0, control.Completed);
    }

    [Fact]
    public void TheScanBarNeverGoesBackwards_AcrossTheWholeMesh()
    {
        var collector = new Collector();
        var control = new RunControl { MinReportIntervalMs = 0, Progress = collector };

        SurfaceMesher.Mesh(Fixture(), PlanarMeshSettings.Default,
                           PlanarEdgeReference.ConductorWidth, control);

        long prev = -1;
        foreach (var p in collector.Seen)
        {
            if (p.StageTotal == 0) { prev = -1; continue; }   // a pre-scan stage resets the counter
            Assert.True(p.StageCompleted >= prev,
                $"scan counter went backwards: {prev} -> {p.StageCompleted}");
            prev = p.StageCompleted;
        }
    }

    [Fact]
    public void CancellingDuringTheScan_AbandonsTheMesh()
    {
        // A token that is threaded but never observed looks identical at the call site — this is the
        // gate that tells the difference.
        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token    = cts.Token,
            // The throttle is off for this test on purpose. On a fixture this small the whole mesh
            // finishes inside one 40 ms window, so with the default throttle NO scan tick is ever
            // delivered and a progress-driven trigger never fires — correct in production (a mesh
            // that fast needs no reporting) and useless as a way to reach the cancellation path.
            MinReportIntervalMs = 0,
            Progress = new DelegateProgress(p => { if (p.StageTotal > 0 && p.StageCompleted >= 1) cts.Cancel(); }),
        };

        Assert.Throws<OperationCanceledException>(() =>
            SurfaceMesher.Mesh(Fixture(), PlanarMeshSettings.Default,
                               PlanarEdgeReference.ConductorWidth, control));
    }

    [Fact]
    public void ANullControl_ProducesTheIdenticalMesh()
    {
        var a = SurfaceMesher.Mesh(Fixture(), PlanarMeshSettings.Default);
        var b = SurfaceMesher.Mesh(Fixture(), PlanarMeshSettings.Default,
                                   PlanarEdgeReference.ConductorWidth, new RunControl());

        Assert.Equal(a.UnknownCount, b.UnknownCount);
        Assert.Equal(a.CellCount, b.CellCount);
    }

    private sealed class DelegateProgress(Action<RunProgress> f) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => f(value);
    }
}
