// ANT-4 — the far field, gated by five oracles in increasing order of what they can catch.
//
// EVERY ORACLE BEFORE THE POWER BALANCE IS INDEPENDENT OF THE MoM SOLVE, and that is the point:
// the pattern is an exact sum over basis coefficients, so the arithmetic between "these are the
// currents" and "this is the field" can be checked to machine precision without ever solving a
// matrix. What the solve contributes is the currents, and those already have their own gates.
//
// THE ORACLES ARE WRITTEN FROM PUBLISHED THEORY, NOT FROM SpectralGreens. An oracle assembled from
// the same functions it is testing proves only that they are self-consistent — and this directory
// has burned nine oracles, twice finding the ORACLE wrong rather than the method (§L8a, §L7b-b), so
// both sides are written independently:
//
//   §5.1  the εᵣ = 1 reduction        — free space plus ONE image, by image theory alone.
//   §5.2  the horizontal dipole over  — the element factors from a SHORTED-STUB input impedance,
//         a grounded slab               which is a different construction from the cross-multiplied
//                                       tan(k_z1h)/k_z1 form SpectralGreens deliberately uses.
//   §5.3  the rooftop transform       — against adaptive Gauss quadrature, 1e-12, exactly as L8c's
//                                       six inner integrals were.
//   §5.3a the STRATIFIED stack        — a hand-built TWO-SECTION cascade, plus the free self-test
//                                       that collapsing the cover to air reproduces §5.2.
//   §5.4  power balance               — ∫U dΩ ≤ P_accepted, reported before it is gated.
//   §5.5  structural                  — no Dcim on the path, the kernel choice is the fill's, the
//                                       refusals are by name, symmetry mirrors.

using System.Numerics;
using System.Runtime.CompilerServices;
using NumFlat;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarFarFieldTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out = output;

    private const double FHz  = 5e9;
    private const double Eta0 = EmConstants.Mu0 * EmConstants.C0;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Fixtures — a hand-built two-cell mesh carrying exactly one rooftop, so a "single current
    // element" is expressible with no solve anywhere near it.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One x-rooftop over two cells, centred on <paramref name="xc"/>, on level 0.</summary>
    private static PlanarMesh OneXRooftop(double xc, double yc, double w, double h)
    {
        double x0 = xc - w, x1 = xc, x2 = xc + w, y0 = yc - 0.5 * h, y1 = yc + 0.5 * h;
        var cells = new[]
        {
            new PlanarCell(0, 0, 0, x0, y0, x1, y1),
            new PlanarCell(0, 1, 0, x1, y0, x2, y1),
        };
        var bases = new[] { new PlanarBasis(0, 0, 1, PlanarBasisDirection.X) };
        return new PlanarMesh(cells, bases, ["Metal"], [x0, x1, x2], [y0, y1]);
    }

    private static PlanarProblem SlabProblem(GroundedSlab slab, double fHz = FHz) =>
        new([new PlanarConductorLayer("Metal", [PlanarLineFixtures.Rect(-1e-3, -1e-3, 1e-3, 1e-3)],
                                     5.8e7, 35e-6)], slab, fHz);

    /// <summary>The same one-level structure written as an EXPLICIT stack, which is what makes
    /// <see cref="PlanarProblem.RequiresGeneralKernel"/> true — §5.3a's free self-test.</summary>
    private static PlanarProblem GeneralOneSlab(GroundedSlab slab, double fHz = FHz) =>
        SlabProblem(slab, fHz) with { MediumStack = LayerStack.FromGroundedSlab(slab) };

    private static Vec<Complex> UnitCurrent(int n)
    {
        var v = new Vec<Complex>(n);
        for (int i = 0; i < n; i++) v[i] = Complex.One;
        return v;
    }

    private static Complex Cis(double t) => new(Math.Cos(t), Math.Sin(t));

    /// <summary>The proper (Im ≤ 0) root, written here so the oracles do not borrow
    /// <c>SpectralGreens.ProperRoot</c>. There is only one physical branch, so this is the one line
    /// the two sides cannot help but share in meaning; they do not share it in code.</summary>
    private static Complex Down(Complex sq)
    {
        var r = Complex.Sqrt(sq);
        return r.Imaginary > 0 ? -r : r;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.3 — the rooftop transform against adaptive quadrature. 1e-12, as L8c's inner integrals.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b><c>∫ f(r) e^{+j(k_x x + k_y y)} dS</c> by tensor Gauss-Legendre on each half separately</b>
    /// — the definition, integrated numerically, sharing nothing with the closed form but the basis
    /// function itself (<see cref="PlanarBasisFunctions.Evaluate"/>, which Tier 0 already tests
    /// against the definition).
    /// </summary>
    private static Complex TransformByQuadrature(PlanarMesh mesh, PlanarBasis b, double kx, double ky,
                                                 int n = 40)
    {
        var (gx, gw) = Quadrature.Nodes(n);
        Complex sum = Complex.Zero;
        foreach (int ci in new[] { b.CellA, b.CellB })
        {
            var c = mesh.Cells[ci];
            double hx = 0.5 * (c.XMax - c.XMin), mx = 0.5 * (c.XMax + c.XMin);
            double hy = 0.5 * (c.YMax - c.YMin), my = 0.5 * (c.YMax + c.YMin);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double x = mx + hx * gx[i], y = my + hy * gx[j];
                    var (fx, fy) = PlanarBasisFunctions.Evaluate(mesh, b, x, y);
                    double f = b.Direction == PlanarBasisDirection.X ? fx : fy;
                    sum += gw[i] * gw[j] * hx * hy * f * Cis(kx * x + ky * y);
                }
        }
        return sum;
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(3.0e2, 0.0)]
    [InlineData(0.0, -7.5e2)]
    [InlineData(2.1e3, 9.4e2)]
    [InlineData(-4.0e4, 1.7e4)]
    public void RooftopTransform_MatchesAdaptiveQuadrature(double kx, double ky)
    {
        // Unequal cell widths, off-origin, so no symmetry can hide a factor.
        var cells = new[]
        {
            new PlanarCell(0, 0, 0, 1.0e-3, -0.4e-3, 1.7e-3, 0.9e-3),
            new PlanarCell(0, 1, 0, 1.7e-3, -0.4e-3, 3.1e-3, 0.9e-3),
        };
        var basisX = new PlanarBasis(0, 0, 1, PlanarBasisDirection.X);
        var meshX  = new PlanarMesh(cells, [basisX], ["M"], [1.0e-3, 1.7e-3, 3.1e-3], [-0.4e-3, 0.9e-3]);

        Complex closed = RooftopSpectrum.Of(meshX, basisX, kx, ky);
        Complex quad   = TransformByQuadrature(meshX, basisX, kx, ky);
        Assert.True((closed - quad).Magnitude <= 1e-12 * Math.Max(1e-30, quad.Magnitude),
            $"x-rooftop: closed {closed} vs quadrature {quad}");

        // The y-rooftop is the same object with the axes exchanged, and it is worth its own case:
        // a transform that silently used the x formula for both would pass every symmetric fixture.
        var cellsY = new[]
        {
            new PlanarCell(0, 0, 0, 1.0e-3, -0.4e-3, 3.1e-3, 0.9e-3),
            new PlanarCell(0, 0, 1, 1.0e-3,  0.9e-3, 3.1e-3, 1.6e-3),
        };
        var basisY = new PlanarBasis(0, 0, 1, PlanarBasisDirection.Y);
        var meshY  = new PlanarMesh(cellsY, [basisY], ["M"], [1.0e-3, 3.1e-3], [-0.4e-3, 0.9e-3, 1.6e-3]);

        Complex closedY = RooftopSpectrum.Of(meshY, basisY, kx, ky);
        Complex quadY   = TransformByQuadrature(meshY, basisY, kx, ky);
        Assert.True((closedY - quadY).Magnitude <= 1e-12 * Math.Max(1e-30, quadY.Magnitude),
            $"y-rooftop: closed {closedY} vs quadrature {quadY}");
    }

    /// <summary>
    /// <b>At k = 0 the transform is the rooftop's own DIPOLE MOMENT</b>, <c>(w_A + w_B)/2</c> ampere
    /// metres per ampere of coefficient — which is the one number that pins the NORMALISATION rather
    /// than the shape. A transform off by the cell area, or by the shared-edge length, is a pattern
    /// that is right in shape and wrong in level, and nothing in a plot shows that.
    /// </summary>
    [Fact]
    public void RooftopTransform_AtZeroWavenumber_IsTheDipoleMoment()
    {
        var mesh  = OneXRooftop(0, 0, 0.6e-3, 0.35e-3);
        double wA = mesh.Cells[0].Width, wB = mesh.Cells[1].Width;
        Complex t = RooftopSpectrum.Of(mesh, mesh.Bases[0], 0, 0);
        Assert.Equal(0.5 * (wA + wB), t.Real, 15);
        Assert.True(Math.Abs(t.Imaginary) < 1e-18, $"a real transform at k = 0; got {t}");
    }

    /// <summary>The small-argument series and the elementary antiderivative must be the same
    /// function. They are compared where BOTH are well conditioned — the elementary form has already
    /// lost half its digits by |kw| = 1e-3, which is exactly why the series exists.</summary>
    [Fact]
    public void RampSeriesAndClosedForm_AgreeWhereBothAreConditioned()
    {
        const double w = 0.7e-3;
        foreach (double t in new[] { 0.5, 0.9, 1.0 })
        {
            double k = t / w;
            Complex series = RooftopSpectrum.Ramp(k, w);
            Complex e = Cis(k * w);
            Complex closed = -Complex.ImaginaryOne * w * e / k + (e - 1.0) / (k * k);
            Assert.True((series - closed).Magnitude <= 1e-13 * closed.Magnitude,
                $"|kw| = {t}: series {series} vs closed {closed}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.1 — the εᵣ = 1 reduction. Free space plus ONE image, and nothing else.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Collapse the slab to free space and the element factors must be exactly the array factor of a
    /// horizontal dipole and its NEGATIVE image: <c>2j sin(k₀h cosθ)</c>, times cosθ for TM. This
    /// catches a sign or branch error in either factor with no substrate physics involved at all —
    /// the direct analogue of the image gate that validated kernel A's R-mom-7.
    /// </summary>
    [Fact]
    public void ElementFactors_WithEpsr1_ReduceToFreeSpacePlusOneImage()
    {
        var slab = new GroundedSlab(1.6e-3, new EmMaterial(1.0));
        var f    = FarFieldElementFactors.For(SlabProblem(slab), FHz);
        double k0 = f.K0, h = slab.HeightM;

        for (int deg = 0; deg <= 90; deg += 3)
        {
            double th = deg * Math.PI / 180.0, cos = Math.Cos(th);
            var (tm, te) = f.At(th, 0);
            Complex af = 2.0 * Complex.ImaginaryOne * Math.Sin(k0 * h * cos);   // image theory, alone
            Assert.True((te - af).Magnitude <= 1e-13, $"TE at {deg}°: {te} vs {af}");
            Assert.True((tm - cos * af).Magnitude <= 1e-13, $"TM at {deg}°: {tm} vs {cos * af}");
        }
    }

    /// <summary>
    /// <b>The whole assembly against image theory.</b> The oracle is a free-space Hertzian dipole
    /// plus its negative image, with the SOURCE INTEGRAL done by quadrature rather than by the closed
    /// form — so the only thing this shares with the code under test is the definition of the basis
    /// function. It exercises the lateral phase reference too: the rooftop is deliberately off the
    /// origin in both x and y, which is where a transform with the wrong exponent sign is visible.
    /// </summary>
    [Fact]
    public void Pattern_WithEpsr1_IsTheImageDipolePattern()
    {
        var slab    = new GroundedSlab(1.6e-3, new EmMaterial(1.0));
        var problem = SlabProblem(slab);
        var mesh    = OneXRooftop(1.3e-3, -0.8e-3, 0.25e-3, 0.3e-3);
        var grid    = new PlanarFarFieldGrid([0, 17, 41, 63, 84, 90], [0, 37, 90, 174, 265, 311]);

        var p  = PlanarFarField.Compute(problem, mesh, UnitCurrent(1), 1, FHz, grid);
        double k0 = 2.0 * Math.PI * FHz / EmConstants.C0, h = slab.HeightM;
        Complex c = -Complex.ImaginaryOne * k0 * Eta0 / (4.0 * Math.PI);

        // ONE scale for the whole grid, not a per-direction one. At θ = 90° both sides are the
        // structural zero of §4 and a relative tolerance there would be comparing two different
        // spellings of nothing (0 against 1e-34).
        double peak = p.PeakFieldV;

        for (int it = 0; it < grid.ThetaDeg.Count; it++)
            for (int ip = 0; ip < grid.PhiDeg.Count; ip++)
            {
                double th = grid.ThetaDeg[it] * Math.PI / 180.0;
                double ph = grid.PhiDeg[ip]   * Math.PI / 180.0;
                double kx = k0 * Math.Sin(th) * Math.Cos(ph), ky = k0 * Math.Sin(th) * Math.Sin(ph);

                // The x-directed source moment, by quadrature, and its image at −h with −moment.
                Complex jx = TransformByQuadrature(mesh, mesh.Bases[0], kx, ky);
                Complex af = Cis(k0 * Math.Cos(th) * h) - Cis(-k0 * Math.Cos(th) * h);

                Complex fth = c * Math.Cos(th) * Math.Cos(ph) * jx * af;
                Complex fph = c * (-Math.Sin(ph)) * jx * af;

                int i = grid.IndexOf(it, ip);
                double scale = peak;
                Assert.True((p.ETheta[i] - fth).Magnitude <= 1e-10 * scale,
                    $"E_θ at ({grid.ThetaDeg[it]}, {grid.PhiDeg[ip]}): {p.ETheta[i]} vs {fth}");
                Assert.True((p.EPhi[i] - fph).Magnitude <= 1e-10 * scale,
                    $"E_φ at ({grid.ThetaDeg[it]}, {grid.PhiDeg[ip]}): {p.EPhi[i]} vs {fph}");
            }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.2 — the key gate: the horizontal dipole over a grounded slab, from a SECOND formulation.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The element factors of a grounded slab, built from the INPUT IMPEDANCE of a shorted stub —
    /// <c>Z_in = jZ₁tan(k_z1 h)</c>, <c>Γ = (Z_in − Z₀)/(Z_in + Z₀)</c>. That is a different
    /// construction from the cross-multiplied <c>tan(k_z1h)/k_z1</c> form <c>SpectralGreens</c> uses
    /// (whose own header records that <c>(Z_b − Z_a)/(Z_b + Z_a)</c> is the spelling it deliberately
    /// avoids), so agreement between the two is a real check on the derivation rather than on a
    /// transcription.
    /// </summary>
    private static (Complex Tm, Complex Te) SlabOracle(GroundedSlab slab, double fHz, double thetaRad)
    {
        double omega = 2.0 * Math.PI * fHz;
        double k0    = omega / EmConstants.C0;
        Complex eps  = slab.Material.EpsComplex;
        double  h    = slab.HeightM;

        double  kz0 = k0 * Math.Cos(thetaRad);
        double  krh = k0 * Math.Sin(thetaRad);
        Complex kz1 = Down(k0 * (Complex)k0 * eps - krh * krh);
        Complex tan = Complex.Tan(kz1 * h);

        Complex z0e = kz0 / (omega * EmConstants.Eps0);
        Complex z1e = kz1 / (omega * EmConstants.Eps0 * eps);
        Complex ge  = (Complex.ImaginaryOne * z1e * tan - z0e) / (Complex.ImaginaryOne * z1e * tan + z0e);

        Complex z0h = omega * EmConstants.Mu0 / kz0;
        Complex z1h = omega * EmConstants.Mu0 / kz1;
        Complex gh  = (Complex.ImaginaryOne * z1h * tan - z0h) / (Complex.ImaginaryOne * z1h * tan + z0h);

        Complex phase = Cis(kz0 * h);
        return (Math.Cos(thetaRad) * (1.0 + ge) * phase, (1.0 + gh) * phase);
    }

    [Theory]
    [InlineData(4.4, 0.02, 1.6e-3, 5e9)]
    [InlineData(12.9, 0.002, 100e-6, 20e9)]
    [InlineData(2.2, 0.0009, 0.8e-3, 10e9)]
    public void ElementFactors_OverAGroundedSlab_MatchTheShortedStubOracle(
        double epsR, double tanD, double hM, double fHz)
    {
        var slab = new GroundedSlab(hM, new EmMaterial(epsR, tanD));
        var f    = FarFieldElementFactors.For(SlabProblem(slab, fHz), fHz);

        // 89° rather than 90°: the ORACLE divides by k_z0, which the code deliberately never does.
        // Grazing itself is checked for finiteness on its own, below.
        for (int deg = 0; deg <= 89; deg += 1)
        {
            double th = deg * Math.PI / 180.0;
            var (tm, te) = f.At(th, 0);
            var (otm, ote) = SlabOracle(slab, fHz, th);
            Assert.True((tm - otm).Magnitude <= 1e-12 * Math.Max(1.0, otm.Magnitude),
                $"TM at {deg}°, εᵣ {epsR}: {tm} vs {otm}");
            Assert.True((te - ote).Magnitude <= 1e-12 * Math.Max(1.0, ote.Magnitude),
                $"TE at {deg}°, εᵣ {epsR}: {te} vs {ote}");
        }
    }

    /// <summary>
    /// <b>The physical form of §5.2: a real rooftop, small enough to be an infinitesimal dipole,
    /// against the closed-form pattern of one.</b> The exact test above pins the element factors;
    /// this one pins that the whole path really is "a current element radiating over a slab", and it
    /// converges at the rate a finite source must — halve the cell and the disagreement falls by 4.
    /// </summary>
    [Fact]
    public void ASmallRooftop_ApproachesTheClosedFormDipole_AtSecondOrder()
    {
        var slab    = GroundedSlab.Fr4Starter;
        var problem = SlabProblem(slab);
        double k0   = 2.0 * Math.PI * FHz / EmConstants.C0;
        double lam  = 2.0 * Math.PI / k0;
        var grid    = new PlanarFarFieldGrid([13, 44, 71], [0, 55, 200]);
        Complex c   = -Complex.ImaginaryOne * k0 * Eta0 / (4.0 * Math.PI);

        double Worst(double w)
        {
            var mesh = OneXRooftop(0, 0, w, w);
            var p    = PlanarFarField.Compute(problem, mesh, UnitCurrent(1), 1, FHz, grid);
            double moment = 0.5 * (mesh.Cells[0].Width + mesh.Cells[1].Width);   // ∫f dS, in A·m
            double worst = 0;
            for (int it = 0; it < grid.ThetaDeg.Count; it++)
                for (int ip = 0; ip < grid.PhiDeg.Count; ip++)
                {
                    double th = grid.ThetaDeg[it] * Math.PI / 180.0;
                    double ph = grid.PhiDeg[ip]   * Math.PI / 180.0;
                    var (tm, te) = SlabOracle(slab, FHz, th);
                    Complex fth = c * tm * moment * Math.Cos(ph);
                    Complex fph = c * te * moment * (-Math.Sin(ph));
                    int i = grid.IndexOf(it, ip);
                    double scale = Math.Max(fth.Magnitude, fph.Magnitude);
                    worst = Math.Max(worst, (p.ETheta[i] - fth).Magnitude / scale);
                    worst = Math.Max(worst, (p.EPhi[i]   - fph).Magnitude / scale);
                }
            return worst;
        }

        double coarse = Worst(lam / 200.0);
        double fine   = Worst(lam / 400.0);
        Assert.True(coarse < 3e-4, $"a λ/200 rooftop should already look like a dipole; got {coarse:E2}");
        Assert.True(fine < coarse / 3.0,
            $"halving the cell must reduce the disagreement ~4×: {coarse:E2} → {fine:E2}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.3a — the STRATIFIED stack: the path every other oracle here misses.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A hand-built TWO-SECTION cascade for a source at the substrate/cover interface: a 1 A shunt
    /// source sees the shorted substrate stub in parallel with the cover section terminated in the
    /// air half-space, and the voltage is carried up through the cover by its own ABCD matrix. It
    /// shares no line of code and no approximation with <c>LayeredSpectralGreens</c>' Möbius ladder.
    /// </summary>
    private static (Complex Tm, Complex Te) CoveredSlabOracle(
        double d1, EmMaterial m1, double d2, EmMaterial m2, double fHz, double thetaRad)
    {
        double omega = 2.0 * Math.PI * fHz, k0 = omega / EmConstants.C0;
        double kz0 = k0 * Math.Cos(thetaRad), krh = k0 * Math.Sin(thetaRad);
        Complex e1 = m1.EpsComplex, e2 = m2.EpsComplex;
        Complex kz1 = Down(k0 * (Complex)k0 * e1 - krh * krh);
        Complex kz2 = Down(k0 * (Complex)k0 * e2 - krh * krh);

        (Complex Z0, Complex Z1, Complex Z2) Imp(bool tm) => tm
            ? (kz0 / (omega * EmConstants.Eps0),
               kz1 / (omega * EmConstants.Eps0 * e1),
               kz2 / (omega * EmConstants.Eps0 * e2))
            : (omega * EmConstants.Mu0 / kz0,
               omega * EmConstants.Mu0 / kz1,
               omega * EmConstants.Mu0 / kz2);

        Complex T(bool tm)
        {
            var (z0, z1, z2) = Imp(tm);
            Complex t1 = Complex.Tan(kz1 * d1), t2 = Complex.Tan(kz2 * d2);
            Complex zDown = Complex.ImaginaryOne * z1 * t1;                         // shorted stub
            Complex zUp   = z2 * (z0 + Complex.ImaginaryOne * z2 * t2)
                               / (z2 + Complex.ImaginaryOne * z0 * t2);             // cover into air
            Complex vSrc  = zDown * zUp / (zDown + zUp);                            // 1 A into the pair
            // ABCD of the cover, load = the air half-space: V_in = V_L(cos + j(Z₂/Z₀)sin).
            Complex vTop  = vSrc / (Complex.Cos(kz2 * d2)
                                    + Complex.ImaginaryOne * (z2 / z0) * Complex.Sin(kz2 * d2));
            return 2.0 * vTop * Cis(kz0 * (d1 + d2)) / z0;
        }

        return (Math.Cos(thetaRad) * T(true), T(false));
    }

    private static PlanarProblem CoveredProblem(double d1, EmMaterial m1, double d2, EmMaterial m2,
                                                double fHz)
    {
        var stack = new LayerStack(Termination.Pec,
                                   [new MediumLayer(d1, m1), new MediumLayer(d2, m2)],
                                   Termination.Air);
        var slab = new GroundedSlab(d1, m1);          // carried, but the stack is what is solved
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [PlanarLineFixtures.Rect(-1e-3, -1e-3, 1e-3, 1e-3)],
                                      5.8e7, 35e-6, ZM: d1)],
            slab, fHz, MediumStack: stack);
    }

    [Theory]
    [InlineData(4.4, 0.02, 3.0, 0.01)]
    [InlineData(9.8, 0.001, 2.2, 0.0)]
    public void ElementFactors_OverACoveredSlab_MatchTheTwoSectionOracle(
        double eps1, double tan1, double eps2, double tan2)
    {
        var m1 = new EmMaterial(eps1, tan1);
        var m2 = new EmMaterial(eps2, tan2);
        const double d1 = 0.8e-3, d2 = 0.5e-3;

        var problem = CoveredProblem(d1, m1, d2, m2, FHz);
        Assert.True(problem.RequiresGeneralKernel, "a covered patch must take the general path");

        var f = FarFieldElementFactors.For(problem, FHz);
        Assert.True(f.IsGeneral);

        for (int deg = 0; deg <= 89; deg += 1)
        {
            double th = deg * Math.PI / 180.0;
            var (tm, te) = f.At(th, 0);
            var (otm, ote) = CoveredSlabOracle(d1, m1, d2, m2, FHz, th);
            Assert.True((tm - otm).Magnitude <= 1e-11 * Math.Max(1.0, otm.Magnitude),
                $"TM at {deg}°: {tm} vs {otm}");
            Assert.True((te - ote).Magnitude <= 1e-11 * Math.Max(1.0, ote.Magnitude),
                $"TE at {deg}°: {te} vs {ote}");
        }
    }

    /// <summary>
    /// <b>The free self-test §5.3a asks for.</b> Collapse the cover to εᵣ = 1 and the general path
    /// must reproduce the one-slab answer — which catches a cascade-ordering or region-indexing
    /// error immediately, because the two go through completely different code.
    /// </summary>
    [Fact]
    public void TheGeneralPath_WithAnAirCover_ReproducesTheOneSlabAnswer()
    {
        var m1 = new EmMaterial(4.4, 0.02);
        const double d1 = 1.6e-3, d2 = 0.9e-3;
        var slab = new GroundedSlab(d1, m1);

        var oneSlab = FarFieldElementFactors.For(SlabProblem(slab), FHz);
        var covered = FarFieldElementFactors.For(CoveredProblem(d1, m1, d2, EmMaterial.Air, FHz), FHz);
        Assert.False(oneSlab.IsGeneral);
        Assert.True(covered.IsGeneral);

        for (int deg = 0; deg <= 89; deg += 1)
        {
            double th = deg * Math.PI / 180.0;
            var (a, b) = oneSlab.At(th, 0);
            var (c, d) = covered.At(th, 0);
            Assert.True((a - c).Magnitude <= 1e-11 * Math.Max(1.0, a.Magnitude), $"TM at {deg}°: {a} vs {c}");
            Assert.True((b - d).Magnitude <= 1e-11 * Math.Max(1.0, b.Magnitude), $"TE at {deg}°: {b} vs {d}");
        }
    }

    /// <summary>
    /// The general kernel expressed on the SAME one-slab medium — <c>LayerStack.FromGroundedSlab</c>
    /// — must reproduce L8's shipped one-slab element factors exactly. This is the pair L9a's D5
    /// gates everywhere else, asked of the far field.
    /// </summary>
    [Fact]
    public void TheTwoSpectralKernels_AgreeOnTheOneSlabCase()
    {
        var slab = GroundedSlab.Fr4Starter;
        var a = FarFieldElementFactors.For(SlabProblem(slab), FHz);
        var b = FarFieldElementFactors.For(GeneralOneSlab(slab), FHz);
        Assert.False(a.IsGeneral);
        Assert.True(b.IsGeneral);

        for (int deg = 0; deg <= 90; deg += 1)
        {
            double th = deg * Math.PI / 180.0;
            var (atm, ate) = a.At(th, 0);
            var (btm, bte) = b.At(th, 0);
            Assert.True((atm - btm).Magnitude <= 1e-11 * Math.Max(1.0, atm.Magnitude), $"TM at {deg}°");
            Assert.True((ate - bte).Magnitude <= 1e-11 * Math.Max(1.0, ate.Magnitude), $"TE at {deg}°");
        }
    }

    /// <summary>
    /// <b>θ = 90° is IN the axis, so both factors must be finite there</b> — and they are only finite
    /// because neither is written with a 1/cosθ or a <c>Z^h = ωµ/k_z</c> in it. The obvious spellings
    /// of both are infinite at grazing; this is the test that keeps them from creeping back.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothElementFactors_AreFiniteAtGrazing(bool general)
    {
        var slab = GroundedSlab.Fr4Starter;
        var f = FarFieldElementFactors.For(general ? GeneralOneSlab(slab) : SlabProblem(slab), FHz);
        var (tm, te) = f.At(Math.PI / 2.0, 0);
        Assert.True(double.IsFinite(tm.Real) && double.IsFinite(tm.Imaginary), $"TM at grazing: {tm}");
        Assert.True(double.IsFinite(te.Real) && double.IsFinite(te.Imaginary), $"TE at grazing: {te}");
        // Over a laterally infinite ground plane a horizontal current radiates nothing along the
        // plane: Γ^h → −1 and cosθ → 0, so both factors vanish. Not asserted as a tolerance on the
        // physics — asserted because a NaN or a 1e300 would sail past a "finite" check on one of them.
        Assert.True(tm.Magnitude < 1e-9, $"TM at grazing should vanish; got {tm}");
        Assert.True(te.Magnitude < 1e-9, $"TE at grazing should vanish; got {te}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.4 — power balance. REPORTED first; only the inequality is gated.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>∫U dΩ over the upper hemisphere ≤ P_accepted, and the shortfall is reported.</b> The four
    /// terms in full are the metrics phase's itemisation; the weaker statement here is still the one
    /// that catches a pattern with the wrong LEVEL — a factor the shape of the pattern cannot show.
    ///
    /// <para><b>The copper term is identically zero in this kernel</b> (the metal is a perfect
    /// conductor and <c>SigmaSm</c> is never read by the fill), so the balance closes optimistically
    /// and the shortfall printed below is dielectric loss plus surface-wave power together. That is
    /// expected, not a defect, and it is exactly why the gate HERE is an inequality. ANT-5 itemises
    /// the shortfall and gates the EQUALITY on a lossless substrate — see
    /// <c>PlanarMetricsTests.ThePowerBalanceCloses_OnALosslessSubstrate</c>.</para>
    /// </summary>
    [Fact]
    public void RadiatedPower_NeverExceedsThePowerAcceptedAtThePort()
    {
        var problem = PlanarLineFixtures.Fr4Line(4e-3, FHz);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var ctx = new PlanarSolveContext(mesh, ports);
        var sol = ctx.SolveAt(PlanarLineFixtures.Kernel(problem.Slab, FHz), FHz);

        var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], ports[0].Number, FHz,
                                             PlanarFarFieldGrid.Hemisphere(2, 2));

        double accepted = 0.5 * sol.Y[0, 0].Real;
        double radiated = pattern.RadiatedPowerW;
        _out.WriteLine(PlanarPowerBudget.For(problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0])
                                        .Caption);
        _out.WriteLine($"N = {mesh.Bases.Count}, accepted {accepted:E4} W, radiated {radiated:E4} W, " +
                       $"ratio {radiated / accepted:P2}");

        Assert.True(accepted > 0, "a driven passive structure must accept power");
        Assert.True(radiated > 0, "an open planar structure radiates something");
        Assert.True(radiated <= accepted * (1.0 + 1e-9),
            $"radiated {radiated:E6} W exceeds accepted {accepted:E6} W — the pattern's LEVEL is wrong");
    }

    /// <summary>
    /// R-res-6 for the sixth phase running: the far field is one more GROUP of cubes on the DataSet a
    /// planar run already returns. The axes are what this pins — <c>[freq, theta, phi, port]</c> with
    /// θ bounded at 90° — because retro-fitting an axis onto a shipped cube is not free.
    /// </summary>
    [Fact]
    public void AFarFieldRun_AddsOneGroupOfCubesToTheSameDataSet()
    {
        var problem = PlanarLineFixtures.Fr4Line(4e-3, FHz);
        var far = new PlanarFarFieldSettings(PlanarFarFieldGrid.Hemisphere(30, 45));
        var result = new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [FHz],
            new PlanarSolveSettings(Deembed: false, FarField: far));

        var cube = result.Data[$"{PlanarFarField.Group}.Etheta"];
        Assert.Equal(4, cube.Axes.Count);
        Assert.Equal(["freq", "theta", "phi", "port"], cube.Axes.Select(a => a.Name).ToArray());
        Assert.Equal("deg", cube.Axes[1].Unit);
        Assert.Equal(90.0, cube.Axes[1].Values[^1]);
        Assert.Equal(2, cube.Axes[3].Values.Length);          // the port axis, from the first commit
        Assert.True(result.Data.Contains($"{PlanarFarField.Group}.U"));
        Assert.True(result.Data.Contains($"{PlanarFarField.Group}.Ephi"));

        // The S cube is untouched: the far field ADDS a group, it does not change the result type.
        Assert.NotNull(result.Data["S"]);
        Assert.Contains(result.Notes, n => n.Contains("θ spans 0…90°"));
        // ANT-5 superseded ANT-4's interim one-line balance note with the itemised BUDGET, because
        // that note's own closing sentence ("this phase does not itemise") stopped being true. The
        // note this asserts is the one the run now carries.
        Assert.Contains(result.Notes, n => n.Contains("Power budget at"));
        _out.WriteLine(string.Join("\n", result.Notes.Where(n => n.Contains("Far field") || n.Contains("Power budget"))));
    }

    /// <summary>A structure the far field cannot do is REPORTED and the sweep still ships — present
    /// and refused, which is this repository's own shape for a diagnostic it cannot produce.</summary>
    [Fact]
    public void ARefusedFarField_DoesNotThrowTheSweepAway()
    {
        var problem = PlanarLineFixtures.Fr4Line(4e-3, FHz);
        // A θ axis reaching past 90°, which the grid refuses by name — see §4. The point is the
        // SHAPE of the outcome, not which refusal fired: the sweep still ships and the reason is
        // carried as a note in the engine's own wording.
        var far = new PlanarFarFieldSettings(new PlanarFarFieldGrid([0, 45, 90, 135, 180], [0, 90]));
        var result = new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [FHz],
            new PlanarSolveSettings(Deembed: false, FarField: far));

        Assert.NotNull(result.Data["S"]);
        Assert.DoesNotContain(PlanarFarField.Group, result.Data.Groups);
        Assert.Contains(result.Notes, n => n.StartsWith("No far field was computed."));
        _out.WriteLine(result.Notes.First(n => n.StartsWith("No far field was computed.")));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.5 — structural.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ant-1 — the far field must not reach the spatial-domain kernel at all.</b> Asserted by
    /// reading the source, because the thing being prevented is a future refactor that innocently
    /// routes it through <c>Dcim</c> and imports a validated-range refusal the far field does not
    /// need and an approximation it does not have.
    /// </summary>
    [Fact]
    public void TheFarFieldSource_NamesNoSpatialDomainKernel()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Engine", "Mom", "PlanarFarField.cs"));
        string code = StripComments(src);
        foreach (string forbidden in new[] { "Dcim", "SommerfeldIntegral", "DcimModel", "StaticGreens" })
            Assert.False(code.Contains(forbidden, StringComparison.Ordinal),
                $"PlanarFarField.cs names {forbidden} outside a comment; the far field needs no " +
                $"spatial-domain kernel and must not inherit one's validated-range refusal.");
    }

    /// <summary>R-ant-2 — the far field takes its kernel choice from the one predicate the FILL takes
    /// it from. A far field that re-derived it could disagree with the currents it is transforming,
    /// silently, and only on a stratified stack.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFarFieldAndTheFill_AgreeOnWhichKernelTheProblemIs(bool general)
    {
        var slab = GroundedSlab.Fr4Starter;
        var problem = general ? GeneralOneSlab(slab) : SlabProblem(slab);
        var f = FarFieldElementFactors.For(problem, FHz);
        Assert.Equal(problem.RequiresGeneralKernel, f.IsGeneral);
    }

    /// <summary>
    /// A structure whose current is mirrored about y = 0 must give a pattern mirrored about the same
    /// plane: <c>E_θ(θ, −φ) = E_θ(θ, φ)</c> and <c>E_φ(θ, −φ) = −E_φ(θ, φ)</c> for x-directed
    /// current. The sign flip on E_φ is the part worth testing — a pattern that mirrored BOTH
    /// components would be a plausible, wrong φ convention.
    /// </summary>
    [Fact]
    public void ASymmetricCurrent_GivesAMirroredPattern()
    {
        var problem = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh    = OneXRooftop(0, 0, 0.4e-3, 0.3e-3);      // on the y = 0 plane, x-directed
        var grid    = new PlanarFarFieldGrid([11, 47, 78], [23, 61, 299, 337]);
        var p = PlanarFarField.Compute(problem, mesh, UnitCurrent(1), 1, FHz, grid);

        foreach (var (a, b) in new[] { (0, 3), (1, 2) })     // 23° ↔ 337°, 61° ↔ 299°
            for (int it = 0; it < grid.ThetaDeg.Count; it++)
            {
                int i = grid.IndexOf(it, a), j = grid.IndexOf(it, b);
                double s = Math.Max(p.ETheta[i].Magnitude, p.EPhi[i].Magnitude);
                Assert.True((p.ETheta[i] - p.ETheta[j]).Magnitude <= 1e-12 * s, "E_θ must mirror");
                Assert.True((p.EPhi[i] + p.EPhi[j]).Magnitude <= 1e-12 * s, "E_φ must mirror with a sign flip");
            }
    }

    /// <summary>The answer must not depend on the core cap — every direction writes its own slot and
    /// reads nothing another writes, so this is bit-identity, not a tolerance.</summary>
    [Fact]
    public void TheParallelisation_IsBitIdenticalToTheSequentialRun()
    {
        var problem = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh    = OneXRooftop(0.2e-3, 0.1e-3, 0.4e-3, 0.3e-3);
        var grid    = PlanarFarFieldGrid.Hemisphere(15, 20);
        var one = PlanarFarField.Compute(problem, mesh, UnitCurrent(1), 1, FHz, grid, maxDegreeOfParallelism: 1);
        var many = PlanarFarField.Compute(problem, mesh, UnitCurrent(1), 1, FHz, grid, maxDegreeOfParallelism: 8);
        for (int i = 0; i < one.U.Count; i++)
        {
            Assert.Equal(one.ETheta[i].Real, many.ETheta[i].Real);
            Assert.Equal(one.ETheta[i].Imaginary, many.ETheta[i].Imaginary);
            Assert.Equal(one.EPhi[i].Real, many.EPhi[i].Real);
            Assert.Equal(one.EPhi[i].Imaginary, many.EPhi[i].Imaginary);
        }
    }

    // ── The refusals, by name ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AThetaBeyond90_IsRefusedByName()
    {
        var v = new PlanarFarFieldGrid([0, 90, 120], [0]).CanUse();
        Assert.False(v.Ok);
        Assert.Contains("laterally INFINITE", v.Reason);
        Assert.Contains("identically zero", v.Reason);
    }

    [Fact]
    public void ACutCell_IsRefusedByName()
    {
        var region = PlanarCellRegion.FromPiece(
            [new EmPoint(0, 0), new EmPoint(0.5e-3, 0), new EmPoint(0.5e-3, 0.3e-3), new EmPoint(0, 0.3e-3)]);
        var cells = new[]
        {
            new PlanarCell(0, 0, 0, 0, 0, 0.6e-3, 0.3e-3, region),
            new PlanarCell(0, 1, 0, 0.6e-3, 0, 1.2e-3, 0.3e-3),
        };
        var mesh = new PlanarMesh(cells, [new PlanarBasis(0, 0, 1, PlanarBasisDirection.X)], ["M"],
                                  [0, 0.6e-3, 1.2e-3], [0, 0.3e-3]);
        var v = PlanarFarField.CanCompute(SlabProblem(GroundedSlab.Fr4Starter), mesh, FHz);
        Assert.False(v.Ok);
        Assert.Contains("CONFORMAL (cut) boundary cells", v.Reason);
        Assert.Contains("Staircase", v.Reason);
    }

    [Fact]
    public void AVerticalBasis_IsRefusedByName()
    {
        var mesh0 = OneXRooftop(0, 0, 0.4e-3, 0.3e-3);
        var mesh  = mesh0 with { Bases = [new PlanarBasis(0, 0, 1, PlanarBasisDirection.Z)] };
        var v = PlanarFarField.CanCompute(SlabProblem(GroundedSlab.Fr4Starter), mesh, FHz);
        Assert.False(v.Ok);
        Assert.Contains("VERTICAL (via or ground-attachment) current", v.Reason);
        Assert.Contains("SERIES VOLTAGE source", v.Reason);
    }

    [Fact]
    public void AStackThatIsNotGroundedBelow_IsRefusedByName()
    {
        var stack = new LayerStack(Termination.Air, [new MediumLayer(1.6e-3, new EmMaterial(4.4, 0.02))],
                                   Termination.Air);
        var problem = SlabProblem(GroundedSlab.Fr4Starter) with { MediumStack = stack };
        var v = PlanarFarField.CanComputeMedium(problem, FHz);
        Assert.False(v.Ok);
        Assert.Contains("0…90°", v.Reason);
    }

    [Fact]
    public void ADielectricTopHalfSpace_IsRefusedByName()
    {
        var stack = new LayerStack(Termination.Pec, [new MediumLayer(1.6e-3, new EmMaterial(4.4, 0.02))],
                                   Termination.OpenTo(new EmMaterial(2.1)));
        var problem = SlabProblem(GroundedSlab.Fr4Starter) with { MediumStack = stack };
        var v = PlanarFarField.CanComputeMedium(problem, FHz);
        Assert.False(v.Ok);
        Assert.Contains("not free space", v.Reason);
    }

    private static string StripComments(string src)
    {
        src = System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var sb = new System.Text.StringBuilder();
        foreach (string line in src.Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;
            int i = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(i >= 0 ? line[..i] : line).Append('\n');
        }
        return sb.ToString();
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root walking up from this test file.");
        return dir!;
    }
}
