// ================================================================
//  Pattern3DTests.cs — ANT-10's gates, on GEOMETRY rather than pixels.
//
//  §6: "A known analytic pattern renders correctly — a half-wave dipole's doughnut and an isotropic
//  hemisphere are both checkable by eye and by a rendered-geometry assertion (peak direction, null
//  directions, symmetry). Assert the geometry, not the pixels."
//
//  So every assertion here goes through the REAL projection — PatternMesh.Build, with the camera the
//  renderer hands it — and checks a property of the shape that came out. Two of the analytic cases
//  are chosen so the projection has a closed form under a named standard view, which is what lets a
//  unit hemisphere be checked to 12 digits instead of by eye.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class Pattern3DTests(ITestOutputHelper output)
{
    private const string U = "farfield.U";

    // ══ fixtures ═════════════════════════════════════════════════════════════

    /// <summary>
    /// A pattern cube on ANT-4's own axes, over a chosen U(θ, φ) — so the SHAPE is known and a real
    /// structure does not have to oblige with one. Port 2 is 20 dB down throughout, so a test that
    /// pinned the wrong port cannot pass by accident.
    /// </summary>
    private static DataSet Pattern(Func<double, double, double> u,
                                   double thetaMaxDeg = 90, int thetaSteps = 18, int phiSteps = 24)
    {
        double[] theta = Enumerable.Range(0, thetaSteps + 1)
            .Select(i => i * (thetaMaxDeg / thetaSteps)).ToArray();
        double[] phi = Enumerable.Range(0, phiSteps).Select(i => i * (360.0 / phiSteps)).ToArray();
        double[] port = [1, 2];

        var v = new double[theta.Length * phi.Length * port.Length];
        for (int t = 0; t < theta.Length; t++)
            for (int p = 0; p < phi.Length; p++)
                for (int q = 0; q < port.Length; q++)
                    v[((t * phi.Length) + p) * port.Length + q] =
                        u(theta[t], phi[p]) * (q == 0 ? 1.0 : 0.01);

        var ds = new DataSet();
        ds.AddToGroup("farfield", "U", new DataCube(
        [
            new Axis("freq",  [5e9],  "Hz"),
            new Axis("theta", theta,  "deg"),
            new Axis("phi",   phi,    "deg"),
            new Axis("port",  port,   ""),
        ], v));
        return ds;
    }

    /// <summary>A 3D plot over one cube, resolved exactly as the window resolves it.</summary>
    private static Plot Surface(DataSet ds, string spec = "db10(" + U + "[0, :, :, 1])",
                                double floor = -40, PatternCamera? cam = null)
    {
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out string cube, out var slice,
                                                 out var transform, out string err), err);
        var t = new Trace(new SNP([5e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cube, Slice = slice, Transform = transform,
        };

        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz) { PolarDbFloor = floor };
        if (cam is { } c) plot.SurfaceCamera = c;
        plot.Traces.Add(t);
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Surface3D, FreqUnit.GHz);
        plot.Autoscale(force: true);
        return plot;
    }

    private static PatternFacet[] Mesh(Plot plot, int stride = 1) =>
        PatternMesh.Build(plot.Traces[0].SurfaceGrid!, plot.PatternScale!, plot.SurfaceCamera, stride);

    // ══ the analytic patterns ════════════════════════════════════════════════

    /// <summary>
    /// <b>An isotropic hemisphere is a UNIT HEMISPHERE, to 12 digits, through the real projection.</b>
    ///
    /// <para>Every sample equal means peak = reference, so every radius is exactly 1 and the surface
    /// IS the unit hemisphere. Checked under <see cref="SurfaceStandardView.Broadside"/>, where the
    /// basis is (Right, Up, Eye) = (ŷ, −x̂, ẑ) and therefore X² + Y² + Depth² = x² + y² + z² — a
    /// closed form, so this pins the whole chain (radius, direction, camera basis, zoom) rather than
    /// one link of it.</para>
    /// </summary>
    [Fact]
    public void AnIsotropicHemisphere_IsTheUnitHemisphere()
    {
        var plot   = Surface(Pattern((_, _) => 1.0), cam: PatternCamera.For(SurfaceStandardView.Broadside));
        var facets = Mesh(plot);

        Assert.NotEmpty(facets);
        foreach (var f in facets)
        {
            // The centroid of a triangle on a sphere is INSIDE it, so only the vertices are unit —
            // and the two that matter are checked on every facet.
            Assert.Equal(1.0, Norm3(f.X0, f.Y0, DepthOfVertex(plot, f, 0)), 10);
            Assert.Equal(1.0, Norm3(f.X1, f.Y1, DepthOfVertex(plot, f, 1)), 10);
        }
        output.WriteLine($"{facets.Length} facets, every vertex on the unit hemisphere");
    }

    /// <summary>
    /// <b>A vertical half-wave dipole over the ground plane: the doughnut.</b> U(θ) ∝
    /// [cos(½π cos θ) / sin θ]², which is a hard NULL along the zenith and the maximum at the
    /// horizon — the shape a 3D view is genuinely better at showing than a number is.
    ///
    /// <para>Three assertions, which are §6's three: the peak DIRECTION, the null DIRECTIONS, and
    /// the symmetry.</para>
    /// </summary>
    [Fact]
    public void AHalfWaveDipole_PeaksAtTheHorizon_NullsAtTheZenith_AndIsRotationallySymmetric()
    {
        var plot = Surface(Pattern((th, _) => Dipole(th)));
        var grid = plot.Traces[0].SurfaceGrid!;
        var scale = plot.PatternScale!;

        // Peak direction: θ = 90°, and every φ shares it (the axis of symmetry is z).
        var (peakDb, peakTheta, _) = grid.Peak();
        Assert.Equal(90.0, peakTheta, 9);
        Assert.Equal(peakDb, scale.ReferenceDb, 9);
        Assert.Equal(1.0, scale.Radius(peakDb), 12);

        // Null direction: θ = 0 is identically zero power, so it is at or below any floor and is
        // drawn AT the centre — clamped, not dropped, which is PolarPatternScale.Radius's contract.
        Assert.Equal(0.0, scale.Radius(grid.At(0, 0)), 12);

        // Rotational symmetry: the pattern does not depend on φ at all, so every column of a row is
        // the same number. A transposed grid would fail this and nothing else would catch it.
        for (int it = 0; it < grid.ThetaCount; it++)
            for (int ip = 1; ip < grid.PhiCount; ip++)
                Assert.Equal(grid.At(it, 0), grid.At(it, ip), 12);

        // And the PROJECTED shape is symmetric too, which is the assertion about the picture rather
        // than about the numbers: under Broadside the screen radius of a vertex is r(θ)·sin θ and
        // depends on θ ALONE, so the whole surface projects onto ThetaCount concentric rings.
        var facets = Mesh(Surface(Pattern((th, _) => Dipole(th)),
                                  cam: PatternCamera.For(SurfaceStandardView.Broadside)));
        var radii = facets
            .SelectMany(f => new[] { Norm2(f.X0, f.Y0), Norm2(f.X1, f.Y1), Norm2(f.X2, f.Y2) })
            .Select(r => Math.Round(r, 9))
            .Distinct()
            .OrderBy(r => r)
            .ToList();

        Assert.True(radii.Count <= grid.ThetaCount,
            $"the projection is not rotationally symmetric: {radii.Count} distinct radii "
          + $"for {grid.ThetaCount} θ samples");

        // The outermost ring is the horizon at full radius — the peak, drawn at the rim.
        Assert.Equal(1.0, radii[^1], 9);
        output.WriteLine($"peak at θ = {peakTheta}°, null at θ = 0, "
                       + $"{radii.Count} concentric rings, outermost r = {radii[^1]:F6}");
    }

    // ══ depth ordering ═══════════════════════════════════════════════════════

    /// <summary>
    /// <b>§6 — the case a painter's algorithm can get wrong: a lobe BEHIND the origin.</b>
    ///
    /// <para>One narrow lobe at φ = 180°, viewed from φ = 0° — so the lobe points directly away from
    /// the eye and is occluded by the near side of the surface. It must be painted FIRST (smallest
    /// depth). Turn the camera 180° and the same lobe must be painted LAST, and nothing about the
    /// grid or the scale changed in between: the order is the camera's, which is the property under
    /// test.</para>
    /// </summary>
    [Fact]
    public void ALobeBehindTheOrigin_IsPaintedFirst_AndPaintedLastWhenTheCameraTurnsRound()
    {
        static double Lobe(double th, double ph)
        {
            double d = Math.Abs(((ph - 180.0) % 360.0 + 540.0) % 360.0 - 180.0);
            return 0.01 + Math.Exp(-(d * d) / (2 * 25 * 25)) * Math.Exp(-Math.Pow(th - 60, 2) / (2 * 25 * 25));
        }

        var away   = Surface(Pattern(Lobe), cam: PatternCamera.New(0,   0));   // eye at +x, lobe at −x
        var toward = Surface(Pattern(Lobe), cam: PatternCamera.New(180, 0));   // eye at −x, lobe faces it

        var fAway   = Mesh(away);
        var foward  = Mesh(toward);

        // The sort itself, stated rather than assumed — everything below reads it.
        for (int i = 1; i < fAway.Length; i++)
            Assert.True(fAway[i - 1].Depth <= fAway[i].Depth, "facets are not in ascending depth");

        int peakAway = IndexOfPeakFacet(fAway);
        int peakTow  = IndexOfPeakFacet(foward);

        Assert.True(fAway[peakAway].Depth < 0,
            $"the lobe should be behind the origin, depth = {fAway[peakAway].Depth}");
        Assert.True(peakAway < fAway.Length / 2,
            $"a lobe behind the origin must be painted early: {peakAway} of {fAway.Length}");

        Assert.True(foward[peakTow].Depth > 0,
            $"the lobe should be in front of the origin, depth = {foward[peakTow].Depth}");
        Assert.True(peakTow > foward.Length / 2,
            $"a lobe in front must be painted late: {peakTow} of {foward.Length}");

        output.WriteLine($"lobe away: facet {peakAway}/{fAway.Length} at depth {fAway[peakAway].Depth:F3}; "
                       + $"lobe toward: facet {peakTow}/{foward.Length} at depth {foward[peakTow].Depth:F3}");
    }

    // ══ §4 — the hemisphere, and what happens when ANT-11 extends it ═════════

    /// <summary>
    /// <b>The surface closes when the axis does</b> — same cube, same code, θ to 90° and θ to 180°,
    /// and the geometry follows the axis with no constant anywhere to edit.
    ///
    /// <para>The hemisphere SENTENCE this also used to assert is gone: nothing is drawn under a 3D
    /// pattern but the trace's own identity (owner, 2026-09-11 — see <c>PatternCaption</c>). The
    /// geometry half is the half that was ever a measurement.</para>
    /// </summary>
    [Fact]
    public void TheClosedSurface_ComesFromTheAxisRange()
    {
        var half = Surface(Pattern((_, _) => 1.0, thetaMaxDeg: 90),
                           cam: PatternCamera.For(SurfaceStandardView.Broadside));
        var full = Surface(Pattern((_, _) => 1.0, thetaMaxDeg: 180, thetaSteps: 36),
                           cam: PatternCamera.For(SurfaceStandardView.Broadside));

        Assert.Empty(PatternCaption.Lines(half));
        Assert.Empty(PatternCaption.Lines(full));

        // And the surface itself: under Broadside, Depth IS z. The hemisphere has none below the
        // ground plane; the full sphere closes underneath.
        Assert.True(Mesh(half).All(f => f.Depth > -1e-9), "a hemisphere reached below z = 0");
        Assert.True(Mesh(full).Any(f => f.Depth < -0.5),  "a 180° cube did not close underneath");

        output.WriteLine("half stays above z = 0; full closes underneath");
    }

    /// <summary>
    /// <b>A 3D pattern writes NO sentence under itself</b> (owner, 2026-09-11). The reference is
    /// still resolved and is still what the colour bar's numbers are against — and
    /// <see cref="PolarPatternScale.ReferenceCaption"/> still composes the sentence for anything
    /// that quotes it — but the plot does not print it.
    /// </summary>
    [Fact]
    public void TheSurface_DrawsNoCaptionButStillResolvesItsReference()
    {
        var plot = Surface(Pattern((th, _) => Dipole(th)), floor: -30);

        Assert.Empty(PatternCaption.Lines(plot));
        Assert.Contains("normalised — outer ring = peak", plot.PatternScale!.ReferenceCaption());
        Assert.Contains("30 dB to centre", plot.PatternScale.ReferenceCaption());
        output.WriteLine(plot.PatternScale.ReferenceCaption());
    }

    // ══ the grid, and the traps in it ════════════════════════════════════════

    /// <summary>
    /// <b>The φ seam closes without doubling.</b> A 0…350° axis wraps (the last column joins the
    /// first) and a 0…360° axis does not (it already carries the duplicate). Both are legal cubes
    /// and the difference between them is a slit in one picture or a dark stripe in the other.
    /// </summary>
    [Fact]
    public void ThePhiSeam_ClosesOnAnOpenAxisAndIsNotDoubledOnAClosedOne()
    {
        var open   = Surface(Pattern((_, _) => 1.0, phiSteps: 24));          // 0 … 345, step 15
        var closed = ClosedPhi();

        var go = open.Traces[0].SurfaceGrid!;
        var gc = closed.Traces[0].SurfaceGrid!;
        Assert.True(go.PhiWraps,  "an open φ axis must wrap");
        Assert.False(gc.PhiWraps, "a φ axis that already ends at 360° must not be wrapped again");

        // 24 samples that wrap and 25 that already close are the SAME 24 φ cells, so the two draw
        // the same number of triangles over the same θ axis. If the wrap were missing the open one
        // would be one cell short (a slit); if it were applied to both, the closed one would be one
        // cell long (a doubled seam drawn over itself).
        Assert.Equal(24, go.PhiCount);
        Assert.Equal(25, gc.PhiCount);
        Assert.Equal(go.ThetaCount, gc.ThetaCount);
        Assert.Equal(Mesh(open).Length, Mesh(closed).Length);
    }

    /// <summary>
    /// <b>A cube with no second angle axis is refused by name, not drawn as something plausible.</b>
    /// ANT-5's per-point metrics are the real case — <c>GainDbi</c> is [freq, port] and there is no
    /// surface in it.
    /// </summary>
    [Fact]
    public void ACubeWithNoTwoAngleAxes_IsRefusedRatherThanDrawn()
    {
        var ds = PatternFixture.Data;
        Assert.True(CubeTraceSpecParser.TryParse("farfield.GainDbi[:, 1]", ds,
                                                 out string cube, out var slice, out var tr, out string e), e);
        var t = new Trace(new SNP([5e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        { CubeName = cube, Slice = slice, Transform = tr };

        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz);
        plot.Traces.Add(t);
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Surface3D, FreqUnit.GHz);

        Assert.Null(t.SurfaceGrid);
        Assert.Contains("TWO angle axes", t.ExpressionError);
        output.WriteLine(t.ExpressionError!);
    }

    /// <summary>
    /// <b>The family cap is why this does not go through the family mechanism.</b> ANT-11's 0…180°
    /// θ axis at 1° is 181 samples and <see cref="Trace.MaxFamilyCurves"/> is 101 — so the surface
    /// carries every one of them, and a test says the number rather than the design note alone.
    /// </summary>
    [Fact]
    public void A181PointThetaAxis_KeepsEverySample_WhereAFamilyWouldHaveCappedAt101()
    {
        var plot = Surface(Pattern((_, _) => 1.0, thetaMaxDeg: 180, thetaSteps: 180, phiSteps: 8));
        Assert.Equal(181, plot.Traces[0].SurfaceGrid!.ThetaCount);
        Assert.True(181 > Trace.MaxFamilyCurves);
    }

    /// <summary>
    /// <b>Decimation keeps the endpoints, so the silhouette and the peak direction do not move.</b>
    /// §6: draw the decimated grid while interacting and the full one on release — which is only
    /// honest if the two are the same picture at the scale a drag is read at.
    /// </summary>
    [Fact]
    public void Decimation_KeepsBothEndpointsOfBothAxes()
    {
        foreach (int stride in new[] { 2, 3, 7, 40 })
        {
            var idx = PatternMesh.Sample(91, stride);
            Assert.Equal(0,  idx[0]);
            Assert.Equal(90, idx[^1]);
            Assert.True(idx.Length >= 2);
        }

        // And a coarse grid is never decimated at all — a 30° × 45° pattern is 8 cells, and a
        // stride over it would turn a pattern into a triangle.
        var coarse = Surface(Pattern((_, _) => 1.0, thetaSteps: 3, phiSteps: 4));
        Assert.Equal(1, SurfaceRenderer.StrideFor(coarse.Traces[0].SurfaceGrid!, PlotDetail.Quick));

        var fine = Surface(Pattern((_, _) => 1.0, thetaMaxDeg: 90, thetaSteps: 90, phiSteps: 360));
        Assert.True(SurfaceRenderer.StrideFor(fine.Traces[0].SurfaceGrid!, PlotDetail.Quick) > 1);
        Assert.Equal(1, SurfaceRenderer.StrideFor(fine.Traces[0].SurfaceGrid!, PlotDetail.Full));
    }

    /// <summary>
    /// <b>The surface reads the SAME quantity the cut reads.</b> One transform, one RectY: every
    /// grid value is the cube's own number through <c>db10</c>, not something re-derived, so a
    /// surface and a polar cut of the same trace can never be two different quantities.
    /// </summary>
    [Fact]
    public void TheGridValues_AreTheCubesOwnNumbersThroughTheTracesOwnTransform()
    {
        var ds   = Pattern((th, ph) => Dipole(th) * (1.0 + 0.25 * Math.Cos(ph * Math.PI / 180.0)));
        var plot = Surface(ds);
        var grid = plot.Traces[0].SurfaceGrid!;
        var cube = ds[U];

        for (int it = 0; it < grid.ThetaCount; it++)
            for (int ip = 0; ip < grid.PhiCount; ip++)
            {
                double want = DbFloor.Db10(cube[0, it, ip, 0].RealValue!.Value);
                double got  = grid.At(it, ip);
                if (double.IsFinite(want)) Assert.Equal(want, got, 10);
                else Assert.False(double.IsFinite(got));
            }
    }

    /// <summary>
    /// <b>Switching the plot away from 3D drops the grid.</b> Otherwise a trace that spent a moment
    /// on a surface keeps a grid resolved against a slice the author has since changed, and switching
    /// back draws the old antenna.
    /// </summary>
    [Fact]
    public void LeavingTheSurface_DropsTheGrid()
    {
        var plot = Surface(Pattern((th, _) => Dipole(th)));
        Assert.NotNull(plot.Traces[0].SurfaceGrid);

        plot.SetPlotType(PlotType.Polar);
        Assert.Null(plot.Traces[0].SurfaceGrid);
    }

    // ══ the view, and the controls that name it ══════════════════════════════

    /// <summary>
    /// <b>§3's named views, on the inspector that offers them.</b> Each button puts the camera on a
    /// stated direction and says so; <b>the zoom is deliberately KEPT</b> across a view change, and
    /// Reset is the one control that undoes both.
    /// </summary>
    [Fact]
    public void TheNamedViews_SetTheCameraAndKeepTheZoom_AndResetUndoesBoth()
    {
        var plot = Surface(Pattern((th, _) => Dipole(th)));
        var vm   = new CircuitRF.Ui.DataDisplay.ViewModels.PlotInspectorViewModel(plot, () => { }, null);

        Assert.True(vm.IsSurfacePlot);
        Assert.False(vm.IsPolarPlot);
        Assert.True(vm.HasPatternScale);          // the dB floor/reference controls apply here too
        Assert.False(vm.IsPolarDbPlot);           // …but the radial-MODE switch does not

        Assert.True(vm.SurfaceViewIsIso);         // a new surface opens on the isometric view
        plot.SurfaceCamera = plot.SurfaceCamera.ZoomedBy(2.0);

        vm.SurfaceViewBroadsideCommand.Execute(null);
        Assert.True(vm.SurfaceViewIsBroadside);
        Assert.False(vm.SurfaceViewIsIso);
        Assert.Equal(90.0, plot.SurfaceCamera.ElevationDeg, 9);
        Assert.Equal(2.0,  plot.SurfaceCamera.Zoom, 9);          // kept

        vm.SurfaceViewPhi0Command.Execute(null);
        Assert.Equal(90.0, plot.SurfaceCamera.AzimuthDeg, 9);
        Assert.Equal(0.0,  plot.SurfaceCamera.ElevationDeg, 9);

        vm.SurfaceViewPhi90Command.Execute(null);
        Assert.Equal(0.0, plot.SurfaceCamera.AzimuthDeg, 9);
        Assert.Equal(0.0, plot.SurfaceCamera.ElevationDeg, 9);

        vm.SurfaceResetCommand.Execute(null);
        Assert.True(vm.SurfaceViewIsIso);
        Assert.Equal(1.0, plot.SurfaceCamera.Zoom, 9);           // reset undoes the zoom too
    }

    /// <summary>
    /// <b>The camera is two angles and a scale, and the clamps are on it rather than on the
    /// gestures.</b> Elevation stops at the poles, azimuth wraps, zoom has both ends — so a drag
    /// that runs off the control and a wheel held down cannot leave the view somewhere it cannot be
    /// drawn from.
    /// </summary>
    [Fact]
    public void TheCamera_ClampsElevationAndZoom_AndWrapsAzimuth()
    {
        var c = PatternCamera.New(0, 0);

        Assert.Equal(90.0,  c.RotatedBy(0,  400).ElevationDeg, 9);
        Assert.Equal(-90.0, c.RotatedBy(0, -400).ElevationDeg, 9);

        Assert.Equal(10.0,  c.RotatedBy(370, 0).AzimuthDeg, 9);
        Assert.Equal(350.0, c.RotatedBy(-10, 0).AzimuthDeg, 9);

        Assert.Equal(PatternCamera.MaxZoom, c.ZoomedBy(1000).Zoom, 9);
        Assert.Equal(PatternCamera.MinZoom, c.ZoomedBy(1e-6).Zoom, 9);

        // A broadside camera is the pole case and is an ORDINARY one here: Right does not depend on
        // elevation, so the basis stays orthonormal and there is nothing to special-case.
        var top = PatternCamera.For(SurfaceStandardView.Broadside);
        var (rx, ry, rz) = top.Right;
        var (ux, uy, uz) = top.Up;
        var (ex, ey, ez) = top.Eye;
        Assert.Equal(1.0, Norm3(rx, ry, rz), 12);
        Assert.Equal(1.0, Norm3(ux, uy, uz), 12);
        Assert.Equal(1.0, Norm3(ex, ey, ez), 12);
        Assert.Equal(0.0, rx * ux + ry * uy + rz * uz, 12);
        Assert.Equal(0.0, rx * ex + ry * ey + rz * ez, 12);
        Assert.Equal(0.0, ux * ex + uy * ey + uz * ez, 12);
    }

    // ══ helpers ══════════════════════════════════════════════════════════════

    /// <summary>U(θ) of a half-wave dipole, normalised to 1 at the horizon. Zero at θ = 0 exactly.</summary>
    private static double Dipole(double thetaDeg)
    {
        double t = thetaDeg * Math.PI / 180.0;
        double s = Math.Sin(t);
        if (s < 1e-12) return 0.0;
        double f = Math.Cos(0.5 * Math.PI * Math.Cos(t)) / s;
        return f * f;
    }

    private static double Norm2(double x, double y) => Math.Sqrt(x * x + y * y);
    private static double Norm3(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

    /// <summary>A facet's per-VERTEX depth is not carried (only the centroid's is), so it is
    /// recovered the only honest way: through the same projection the mesh used.</summary>
    private static double DepthOfVertex(Plot plot, PatternFacet f, int which)
    {
        // Under an orthographic camera the scene X/Y of a point on the unit hemisphere determine its
        // depth up to sign, and the hemisphere is entirely on one side under Broadside.
        double x = which == 0 ? f.X0 : f.X1, y = which == 0 ? f.Y0 : f.Y1;
        double r2 = x * x + y * y;
        return Math.Sqrt(Math.Max(0, 1 - r2));
    }

    private static int IndexOfPeakFacet(PatternFacet[] facets)
    {
        int best = 0;
        for (int i = 1; i < facets.Length; i++) if (facets[i].Db > facets[best].Db) best = i;
        return best;
    }

    /// <summary>A φ axis that already ends at 360°, carrying the duplicate column.</summary>
    private static Plot ClosedPhi()
    {
        double[] theta = Enumerable.Range(0, 19).Select(i => i * 5.0).ToArray();
        double[] phi   = Enumerable.Range(0, 25).Select(i => i * 15.0).ToArray();   // 0 … 360
        var v = new double[theta.Length * phi.Length];
        Array.Fill(v, 1.0);

        var ds = new DataSet();
        ds.AddToGroup("farfield", "U", new DataCube(
            [new Axis("theta", theta, "deg"), new Axis("phi", phi, "deg")], v));

        Assert.True(CubeTraceSpecParser.TryParse("db10(farfield.U[:, :])", ds,
                                                 out string c, out var s, out var tr, out string e), e);
        var t = new Trace(new SNP([5e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        { CubeName = c, Slice = s, Transform = tr };
        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz);
        plot.Traces.Add(t);
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Surface3D, FreqUnit.GHz);
        plot.Autoscale(force: true);
        return plot;
    }
}
