// LFE — the grounded (true) edge port, and the low-frequency wall it removes.
//
// The series delta gap's other terminal is the floating outermost cell, so a₂₁ ∝ ω and the peel
// amplifies every raw error by 1/|a₂₁|² at the bottom of a band — on a 5 mm 50 Ω laminate line the
// published Zc was 0.9 Ω at 100 MHz and the de-embedded answer moved with the mesh. The grounded port
// drops the outer pulse of every port rooftop, so the source returns through the ground plane and the
// port box stays a small lumped section at every frequency. See PlanarGroundedEdgePort.cs.
//
// Gated here:
//   G1  THE ALGEBRA. The Woodbury solve IS the explicitly modified system Z' = Z + σ(−UWᵀ − WUᵀ + UQUᵀ),
//       built entry by entry from the fill's own P — to round-off.
//   G2  THE PORT. Raw |S₂₁| through a short line at 100 MHz: ~1 grounded, ~1e-4 series.
//   G3  THE BOX. A synthetic lumped box behind a known line is recovered by SolveLumpedErrorBox
//       exactly enough that the de-embedded standard is the bare line.
//   G4  MESH INVARIANCE. A de-embedded uniform line's Zc and εeff agree between a coarse and a fine
//       mesh below 2 GHz, where the series gap's did not (G4 and its cheaper G4r are Benchmark-tier).
//   G5  (Benchmark) the W 890.7 µm laminate line is flat from 0.1 to 6 GHz.
//   G6  the accelerated operator takes the same port.  G7 (Benchmark) a via-bearing two-level line.
//   G8  the run grounds its ports only below the quasi-static crossover, and stays continuous across it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class GroundedEdgePortTests(ITestOutputHelper output)
{
    /// <summary>The validation stack of the series: 367 µm, εᵣ 3.00, tanδ 0.0015 — one slab.</summary>
    private static GroundedSlab Laminate => new(367e-6, new EmMaterial(3.0, 0.0015));

    private const double LaminateWidthM = 890.7e-6;

    private static PlanarSolveSettings Settings(PlanarEdgePortReturn ret) =>
        PlanarSolveSettings.Default with
        {
            Fill = PlanarFillSettings.Default with { EdgePortReturn = ret },
        };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G1 — the Woodbury update is the modified system, entry by entry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G1_TheWoodburySolve_IsTheExplicitlyModifiedSystem()
    {
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.Line(slab, PlanarLineFixtures.Fr4HeroWidthM, 6e-3, 2e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem);
        double f = 300e6, omega = 2 * Math.PI * f;

        var cores = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default);
        var k = PlanarLineFixtures.Kernel(slab, f).For(cores, PlanarFillSettings.Default.Order);
        var z = PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, omega);
        var p = PlanarFill.ScalarPotentialMatrix(cores, k.Scalar);

        // The explicit Z': D' = D − U Sᵀ, so the scalar block changes by σ(D'PD'ᵀ − DPDᵀ).
        int n = mesh.Bases.Count, cells = mesh.Cells.Count;
        var terms = PlanarGroundedEdgePort.Terminals(mesh, ports);
        Assert.Equal(ports.Sum(q => q.BasisCount), terms.Count);
        var d  = new double[n, cells];
        var d2 = new double[n, cells];
        for (int i = 0; i < n; i++)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);
            d[i, ha.CellIndex] += ha.Sign; d[i, hb.CellIndex] += hb.Sign;
            d2[i, ha.CellIndex] += ha.Sign; d2[i, hb.CellIndex] += hb.Sign;
        }
        foreach (var t in terms) d2[t.Basis, t.OuterCell] -= t.Sign;

        Complex sigma = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        var zp = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex delta = Complex.Zero;
                for (int a = 0; a < cells; a++)
                {
                    if (d[i, a] == 0 && d2[i, a] == 0) continue;
                    for (int b = 0; b < cells; b++)
                    {
                        double w = d2[i, a] * d2[j, b] - d[i, a] * d[j, b];
                        if (w != 0) delta += w * p[a, b];
                    }
                }
                zp[i, j] = z[i, j] + sigma * delta;
            }

        // Reference: solve Z' directly.
        var lu = zp.Lu();
        var yRef = new Complex[2, 2];
        for (int j = 0; j < 2; j++)
        {
            var x = lu.Solve(PlanarExcitation.RightHandSide(n, ports[j]));
            for (int i = 0; i < 2; i++) yRef[i, j] = PlanarExcitation.PortCurrent(x, ports[i]);
        }

        // Under test: Woodbury against the UNMODIFIED Z, with P read on demand as the solve does.
        var pulse = new PlanarPulsePotential(new PlanarEntryCores(cores), k.Scalar);
        var sol = PlanarGroundedEdgePort.Solve(PlanarSystem.Wrap(z.Copy()), mesh, ports, pulse.At, omega);

        double scale = 0, worst = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                scale = Math.Max(scale, yRef[i, j].Magnitude);
                worst = Math.Max(worst, (sol.Y[i, j] - yRef[i, j]).Magnitude);
            }
        output.WriteLine($"N = {n}, K = {terms.Count}, |Y| = {scale:e3}, worst |ΔY| = {worst:e3}");
        Assert.True(worst < 1e-9 * scale, $"Woodbury vs explicit Z': {worst:e3} against |Y| {scale:e3}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G2 — the raw port transmits at the bottom of the band
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G2_TheRawGroundedPort_TransmitsAt100MHz_WhereTheSeriesGapDoesNot()
    {
        var problem = PlanarLineFixtures.Line(Laminate, LaminateWidthM, 5e-3, 6e9);
        var ports = PlanarLineFixtures.EndPorts(problem);
        var mesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3);

        double S21(PlanarEdgePortReturn ret)
        {
            var st = Settings(ret) with { Deembed = false };
            var r = new PlanarKernel().Solve(problem, mesh, ports, [100e6], st);
            return r.Solve.Points[0].RawS[1, 0].Magnitude;
        }

        double grounded = S21(PlanarEdgePortReturn.Ground), series = S21(PlanarEdgePortReturn.SeriesGap);
        output.WriteLine($"raw |S21| at 100 MHz: grounded {grounded:F6}, series gap {series:e3}");
        Assert.InRange(grounded, 0.99, 1.0 + 1e-9);
        Assert.True(series < 1e-3, $"series gap raw |S21| {series:e3}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G3 — the lumped box is recovered, and de-embeds a standard to the bare line
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(100e6)]
    [InlineData(1e9)]
    [InlineData(6e9)]
    public void G3_ALumpedBoxBehindAKnownLine_IsRecovered(double fHz)
    {
        double w = 2 * Math.PI * fHz;
        Complex zs = new(0.02, -w * 49e-12), yp = new(1e-7, w * 15e-15);   // the measured box's size
        Complex zc = new(51.2, -0.3), z0 = 50.0;
        Complex gamma = new(0.012, w * Math.Sqrt(2.43) / 299792458.0);
        double l1 = 2.2e-3, l2 = 3.3e-3;

        Mat<Complex> Standard(double len)
        {
            // An independent cascade: ABCD of box · line · mirrored box, then S at z0 both ends.
            Complex ch = Complex.Cosh(gamma * len), sh = Complex.Sinh(gamma * len);
            var box = new[,] { { 1 + zs * yp, zs }, { yp, Complex.One } };
            var lin = new[,] { { ch, zc * sh }, { sh / zc, ch } };
            var mir = new[,] { { Complex.One, zs }, { yp, 1 + yp * zs } };
            var t = Mul(Mul(box, lin), mir);
            Complex den = t[0, 0] * z0 + t[0, 1] + t[1, 0] * z0 * z0 + t[1, 1] * z0;
            var s = new Mat<Complex>(2, 2);
            s[0, 0] = (t[0, 0] * z0 + t[0, 1] - t[1, 0] * z0 * z0 - t[1, 1] * z0) / den;
            s[1, 1] = (-t[0, 0] * z0 + t[0, 1] - t[1, 0] * z0 * z0 + t[1, 1] * z0) / den;
            s[0, 1] = s[1, 0] = 2 * z0 / den;
            return s;
        }

        var sShort = Standard(l1);
        var box = LineDeembed.SolveLumpedErrorBox(sShort, Standard(l2), l1, l2, gamma, zc, z0);
        var bare = LineDeembed.Apply(sShort, [box, box]);
        Complex x = Complex.Exp(-gamma * l1);

        output.WriteLine($"{fHz / 1e9:g} GHz: a11 {box.A11:e3}  a21 {box.A21}  residual×|a11| " +
                         $"{box.ConsistencyResidual * box.A11.Magnitude:e3}");
        Assert.True(bare[0, 0].Magnitude < 1e-9, $"de-embedded S11 {bare[0, 0].Magnitude:e3}");
        Assert.True((bare[1, 0] - x).Magnitude < 1e-9, $"de-embedded S21 off by {(bare[1, 0] - x).Magnitude:e3}");
        Assert.True(box.DeembedErrorFloor < 1e-9);
    }

    private static Complex[,] Mul(Complex[,] a, Complex[,] b) => new[,]
    {
        { a[0, 0] * b[0, 0] + a[0, 1] * b[1, 0], a[0, 0] * b[0, 1] + a[0, 1] * b[1, 1] },
        { a[1, 0] * b[0, 0] + a[1, 1] * b[1, 0], a[1, 0] * b[0, 1] + a[1, 1] * b[1, 1] },
    };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G4 — mesh invariance below 2 GHz
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Zc and εeff of a de-embedded uniform line, from its own ABCD at 50 Ω.</summary>
    private static (double Zc, double Eeff) Line(Mat<Complex> s, double lengthM, double fHz)
    {
        Complex s11 = s[0, 0], s21 = s[1, 0], s12 = s[0, 1], s22 = s[1, 1], z0 = 50.0;
        Complex a = ((1 + s11) * (1 - s22) + s12 * s21) / (2 * s21);
        Complex b = z0 * ((1 + s11) * (1 + s22) - s12 * s21) / (2 * s21);
        Complex c = ((1 - s11) * (1 - s22) - s12 * s21) / (2 * s21 * z0);
        Complex zc = Complex.Sqrt(b / c);
        double beta = Complex.Log(a + Complex.Sqrt(a * a - 1)).Imaginary / lengthM;
        double eeff = Math.Pow(Math.Abs(beta) * 299792458.0 / (2 * Math.PI * fHz), 2);
        return (zc.Real, eeff);
    }

    private (double Zc, double Eeff)[] Sweep(PlanarMeshSettings mesh, PlanarEdgePortReturn ret, double[] freqs,
                                            double lengthM = 5e-3)
    {
        var problem = PlanarLineFixtures.Line(Laminate, LaminateWidthM, lengthM, 6e9);
        var r = new PlanarKernel().Solve(problem, mesh, PlanarLineFixtures.EndPorts(problem), freqs, Settings(ret));
        return [.. r.Solve.Points.Select(p => Line(p.S, lengthM, p.FrequencyHz))];
    }

    /// <summary>
    /// The routine-tier form of G4, on the calibrator path the PEEL gates use (one cached kernel fit per
    /// frequency rather than the full kernel's): a 5 mm line at 100 MHz and 1 GHz on two meshes.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]       // ~16 s in Debug: four meshes' worth of standards
    public void G4r_ADeembeddedLine_IsMeshInvariantAtTheBottomOfTheBand()
    {
        double[] freqs = [100e6, 1e9];
        var a = Calibrated(new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: true, EdgeCells: 2), freqs);
        var b = Calibrated(new PlanarMeshSettings(Auto: false, CellsPerWavelength: 30, EdgeMesh: true, EdgeCells: 4), freqs);
        for (int i = 0; i < freqs.Length; i++)
        {
            output.WriteLine($"{freqs[i] / 1e9,5:g} GHz  coarse Zc {a[i].Zc:F3} εeff {a[i].Eeff:F4}   " +
                             $"fine Zc {b[i].Zc:F3} εeff {b[i].Eeff:F4}");
            Assert.InRange(a[i].Zc / b[i].Zc, 0.99, 1.01);
            Assert.InRange(a[i].Eeff / b[i].Eeff, 0.98, 1.02);
            Assert.InRange(b[i].Zc, 49.0, 54.0);
            Assert.InRange(b[i].Eeff, 2.25, 2.6);
        }
    }

    private (double Zc, double Eeff)[] Calibrated(PlanarMeshSettings meshSettings, double[] freqs,
                                                 double lengthM = 5e-3)
    {
        var slab = Laminate;
        var problem = PlanarLineFixtures.Line(slab, LaminateWidthM, lengthM, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, meshSettings);
        double fLo = freqs.Min(), fHi = freqs.Max();
        var fill = PlanarFillSettings.Default with
        {
            ConductorLoss  = PlanarConductorLoss.For(problem),
            EdgePortReturn = PlanarEdgePortReturn.Ground,
        };
        var cals = ports.Select(p =>
        {
            var plan = PlanarCalibration.SeparationPlan(slab, fLo, fHi, p);
            var set  = PlanarCalibration.BuildSet(p, slab, plan, PlanarCalibration.SuggestLengths(slab, fLo, fHi).Short);
            return new PlanarPortCalibrator(p, slab, fLo, fHi, null, fill, standards: set, separations: plan);
        }).ToArray();
        var dut = new PlanarSolveContext(mesh, ports, fill, null, slab.HeightM);

        var outv = new List<(double, double)>();
        foreach (double f in freqs.OrderBy(x => x))
        {
            var kernel = PlanarLineFixtures.Kernel(slab, f);
            var c = cals.Select((cal, i) => cal.At(kernel, f, i + 1)).ToArray();
            var atZc = LineDeembed.Apply(dut.RawScatteringAt(kernel, f), [c[0].Box, c[1].Box]);
            var s = LineDeembed.RenormalisePseudoWave(atZc, [c[0].Zc, c[1].Zc], [50.0, 50.0]);
            outv.Add(Line(s, lengthM, f));
        }
        return [.. outv];
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void G4_ADeembeddedLine_IsMeshInvariantBelow2GHz()
    {
        double[] freqs = [100e6, 300e6, 1e9, 2e9];
        var coarse = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3);
        var fine   = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 60, EdgeMesh: true, EdgeCells: 5);

        var c = Sweep(coarse, PlanarEdgePortReturn.Ground, freqs);
        var f = Sweep(fine,   PlanarEdgePortReturn.Ground, freqs);
        for (int i = 0; i < freqs.Length; i++)
            output.WriteLine($"{freqs[i] / 1e9,5:g} GHz  coarse Zc {c[i].Zc:F3} εeff {c[i].Eeff:F4}   " +
                             $"fine Zc {f[i].Zc:F3} εeff {f[i].Eeff:F4}");
        for (int i = 0; i < freqs.Length; i++)
        {
            Assert.InRange(c[i].Zc / f[i].Zc, 0.99, 1.01);
            Assert.InRange(c[i].Eeff / f[i].Eeff, 0.985, 1.015);
            // and against the closed-form neighbourhood of a 51 Ω line on this stack, so a common-mode
            // collapse (both meshes reading the same wrong thing) cannot pass
            Assert.InRange(f[i].Zc, 49.0, 54.0);
            Assert.InRange(f[i].Eeff, 2.3, 2.6);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G5 — the validation line, flat across the band (Benchmark: a fine mesh over 13 points)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void G5_TheValidationLine_IsFlatFrom100MHzTo6GHz_AndTheSeriesGapIsNot()
    {
        double[] freqs = [100e6, 200e6, 500e6, 1e9, 2e9, 3.5e9, 6e9];
        var fine = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 120, EdgeMesh: true, EdgeCells: 7);

        var g = Sweep(fine, PlanarEdgePortReturn.Ground, freqs);
        var s = Sweep(fine, PlanarEdgePortReturn.SeriesGap, freqs);
        for (int i = 0; i < freqs.Length; i++)
            output.WriteLine($"{freqs[i] / 1e9,5:g} GHz  grounded Zc {g[i].Zc:F3} εeff {g[i].Eeff:F4}   " +
                             $"series Zc {s[i].Zc:F3} εeff {s[i].Eeff:F4}");

        double zMin = g.Min(x => x.Zc), zMax = g.Max(x => x.Zc);
        double eMin = g.Min(x => x.Eeff), eMax = g.Max(x => x.Eeff);
        Assert.True(zMax - zMin < 1.0, $"grounded Zc spans {zMin:F2}-{zMax:F2} Ω");
        Assert.True(eMax / eMin < 1.02, $"grounded εeff spans {eMin:F4}-{eMax:F4}");
        // The series gap's own wall, recorded so the contrast cannot quietly disappear: at 100 MHz its
        // de-embedded line is off by more than the whole grounded band.
        Assert.True(Math.Abs(s[0].Zc - g[0].Zc) > 1.0, $"series {s[0].Zc:F2} vs grounded {g[0].Zc:F2} Ω");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G6 — the accelerated operator takes the same grounded port
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G6_TheAcceleratedSolve_AgreesWithTheDenseOne()
    {
        var problem = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var fill = PlanarFillSettings.Default with { EdgePortReturn = PlanarEdgePortReturn.Ground };
        var dense = new PlanarSolveContext(mesh, ports, fill);
        var accel = new PlanarSolveContext(mesh, ports, fill with { Aim = PlanarAimSettings.Default },
                                           slabHeightM: problem.Slab.HeightM);
        double f = 300e6;
        var k = PlanarLineFixtures.Kernel(problem.Slab, f);
        var yd = dense.SolveAt(k, f).Y;
        var ya = accel.SolveAt(k, f).Y;
        double scale = 0, worst = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                scale = Math.Max(scale, yd[i, j].Magnitude);
                worst = Math.Max(worst, (ya[i, j] - yd[i, j]).Magnitude);
            }
        output.WriteLine($"|Y| {scale:e3}, accelerated vs dense {worst:e3}");
        Assert.NotNull(accel.LastAccelerator);
        Assert.True(worst < 1e-4 * scale, $"{worst:e3} against {scale:e3}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G7 — a two-level, via-bearing structure runs with the grounded port: a 400 µm near-thru
    //      reads as one. (The series gap reads |S21| = 0.003 at 5 GHz on this coarse mesh — the
    //      output records it; it is the wall, not a reference.)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static PlanarPolygon R(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    [Fact]
    [Trait("Category", "Benchmark")]       // ~40 s in Debug: a via-bearing fill per frequency
    public void G7_AViaBearingTwoLevelLine_RunsGrounded_AsTheNearThruItIs()
    {
        var stack = LayerStacks.MmicTwoLevel;
        double len = 400e-6;
        var lower = new PlanarConductorLayer("M1", [R(0, 0, len, 100e-6)], 4.1e7, 2e-6, stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("M2", [R(0, 0, len, 100e-6)], 4.1e7, 3e-6, stack.TopZ);
        var via   = new PlanarVia(0, 1, [R(0.45 * len, 30e-6, 0.55 * len, 70e-6)], 4.1e7);
        var problem = new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, 10e9, null, stack, [via]);
        PlanarPort[] ports =
        [
            new PlanarPort(1, new EmPoint(0,   50e-6), PlanarPortSide.MinX, 50.0, 0),
            new PlanarPort(2, new EmPoint(len, 50e-6), PlanarPortSide.MaxX, 50.0, 0),
        ];
        double[] freqs = [5e9, 10e9];

        Mat<Complex>[] Run(PlanarEdgePortReturn ret) =>
            [.. new PlanarKernel().Solve(problem, PlanarLineFixtures.Coarse, ports, freqs, Settings(ret))
                .Solve.Points.Select(p => p.S)];

        var g = Run(PlanarEdgePortReturn.Ground);
        var s = Run(PlanarEdgePortReturn.SeriesGap);
        for (int k = 0; k < freqs.Length; k++)
        {
            double d = 0;
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) d = Math.Max(d, (g[k][i, j] - s[k][i, j]).Magnitude);
            output.WriteLine($"{freqs[k] / 1e9:g} GHz  grounded |S21| {g[k][1, 0].Magnitude:F5}  series " +
                             $"{s[k][1, 0].Magnitude:F5}  max|ΔS| {d:e3}");
            Assert.InRange(g[k][1, 0].Magnitude, 0.95, 1.0 + 1e-3);
            Assert.True(g[k][0, 0].Magnitude < 0.3, $"|S11| {g[k][0, 0].Magnitude:F4}");
            Assert.True((g[k][0, 1] - g[k][1, 0]).Magnitude < 1e-6, "reciprocity");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // G8 — the port is grounded only below the quasi-static crossover. Above it the grounded
    //      source couples port to port through substrate modes and the two-line γ measured on the
    //      standards reads α 2.4× high (7.1 against 2.96 Np/m at 6 GHz on 1.6 mm FR-4), so the run
    //      switches to the series gap there, DUT and standards together. A sweep across the switch
    //      agrees with the all-series-gap run where that one is well conditioned.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G8_ASweepAcrossTheCrossover_SwitchesPortTypes_AndStaysContinuous()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 6e9);
        var ports = PlanarLineFixtures.EndPorts(problem);
        double cross = PlanarCalibration.QuasiStaticCrossoverHz(problem.Slab);
        double[] freqs = [0.8 * cross, 0.95 * cross, 1.05 * cross, 2.0 * cross];
        PlanarFrequencyPoint[] Run(PlanarEdgePortReturn ret) =>
            [.. new PlanarKernel().Solve(problem, PlanarLineFixtures.Coarse, ports, freqs, Settings(ret)).Solve.Points];

        var g = Run(PlanarEdgePortReturn.Ground);
        var s = Run(PlanarEdgePortReturn.SeriesGap);
        for (int k = 0; k < freqs.Length; k++)
        {
            double d = 0;
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) d = Math.Max(d, (g[k].S[i, j] - s[k].S[i, j]).Magnitude);
            var cg = g[k].Calibrations[0];
            output.WriteLine($"{freqs[k] / 1e9:F3} GHz  {cg.Source,-11}  grounded |S21| {g[k].S[1, 0].Magnitude:F5}  " +
                             $"series {s[k].S[1, 0].Magnitude:F5}  max|ΔS| {d:e3}  floors {cg.Box.DeembedErrorFloor:e2} / " +
                             $"{s[k].Calibrations[0].Box.DeembedErrorFloor:e2}");
            Assert.Equal(freqs[k] < cross ? PlanarCalibrationSource.QuasiStatic : PlanarCalibrationSource.Measured, cg.Source);
            // grounded below the crossover: the raw port transmits; above it the series gap's raw does not
            Assert.Equal(freqs[k] < cross, g[k].RawS[1, 0].Magnitude > 0.5);
            Assert.True(d < 0.02, $"grounded-mode vs series run at {freqs[k] / 1e9:F3} GHz: {d:e3}");
        }
    }
}
