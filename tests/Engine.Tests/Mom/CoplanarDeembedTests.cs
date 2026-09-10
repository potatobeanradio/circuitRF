// RP-2c — DE-EMBEDDING A COPLANAR EDGE PORT.
//
// RP-2a shipped the two-cut port and refused an EDGE one by name, because an edge port has a feed
// outside its cut and the standards PlanarCalibration built were uniform single conductors over the
// ground plane. This file is that refusal's arrival: the standard is now the port's own
// cross-section — both conductors and the slot between them — and the calibration ALGEBRA is
// untouched (R-rp2c-3).
//
// The order is the brief's own. Gate 1 first, because every existing de-embedded number in the
// L8/L9 acceptance set goes through the single-conductor builder and none of the rest is worth
// having if that moved. Then the two measurements that say the standard IS the port's neighbourhood
// — the exactness at a calibration length, and γ from a route that shares no algebra with the
// calibration — then the closed form, the mixed run, and the refusals.
//
// EVERY FIXTURE HERE IS EDGE-MESH-OFF AND TRANSMISSION-LINE-MESHED, and both are deliberate:
//   • the transmission-line mesh makes the two pitch controls orthogonal, which is what keeps a
//     20 mm standard of a 0.75 mm-wide pair down to tens of unknowns instead of thousands;
//   • the edge fan is off because it costs the exactness of gate 2 — and that is NOT RP-2c's doing.
//     Measured on a plain GROUND-REFERENCED microstrip at the same settings: |S₁₁| on its own
//     calibration length is 1.0e-15 with the fan off and 1.6e-4 with it on, and on the FR-4 hero at
//     the shipping mesh it is 1.3e-5. It is L8d's own recorded note (the two ends' grading is not
//     exactly mirror-symmetric, so symmetrising the standard's S discards something real), and the
//     coplanar pair inherits it unchanged rather than adding to it.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class CoplanarDeembedTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Fixtures
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Coplanar strips in AIR — strip width <paramref name="w"/>, slot <paramref name="s"/>,
    /// centred on y = 0, running x = 0 → <paramref name="lengthM"/>. RP-2a's own gate-2 fixture,
    /// lengthened so it has a feed to calibrate.</summary>
    private static PlanarProblem Cps(double w, double s, double h, double lengthM, double fHz) =>
        PlanarLineFixtures.Problem(
            new GroundedSlab(h, new EmMaterial(1.0, 0.0)), fHz,
            PlanarLineFixtures.Rect(0,  0.5 * s,       lengthM,  0.5 * s + w),
            PlanarLineFixtures.Rect(0, -(0.5 * s + w), lengthM, -0.5 * s));

    private static PlanarMeshSettings Mesh(int cellsPerWavelength, int across, bool edge = false) =>
        new(Auto: false, CellsPerWavelength: cellsPerWavelength, EdgeMesh: edge,
            MinCellsAcrossConductor: across, CurrentModel: PlanarCurrentModel.TransmissionLine);

    /// <summary>The two ends of a coplanar pair, each a two-cut EDGE port.</summary>
    private static PlanarPort[] CoplanarEnds(double lengthM, double w, double s, double z0 = 50.0)
    {
        double yc = 0.5 * s + 0.5 * w;
        return
        [
            new PlanarPort(1, new EmPoint(0, yc), PlanarPortSide.MinX, z0,
                           Reference: PlanarPortReference.CoplanarGround,
                           NegativeLocation: new EmPoint(0, -yc)),
            new PlanarPort(2, new EmPoint(lengthM, yc), PlanarPortSide.MaxX, z0,
                           Reference: PlanarPortReference.CoplanarGround,
                           NegativeLocation: new EmPoint(lengthM, -yc)),
        ];
    }

    /// <summary>K(k) by the AGM — RP-2a's own, and independent of everything in this repository.</summary>
    private static double EllipticK(double k)
    {
        double a = 1.0, b = Math.Sqrt(1.0 - k * k);
        for (int i = 0; i < 60 && Math.Abs(a - b) > 1e-16 * a; i++) (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return Math.PI / (a + b);
    }

    /// <summary>Z₀ = η₀·K(k)/K(k′), k = S/(S+2W) — coplanar strips in a homogeneous medium, exact for
    /// zero-thickness perfect conductors. RP-2a's gate 2 used this and §3's gate 4 asks for it again,
    /// now with the feed removed.</summary>
    private static double CpsZ0(double w, double s)
    {
        double k = s / (s + 2 * w);
        const double eta0 = EmConstants.Mu0 * EmConstants.C0;
        return eta0 * EllipticK(k) / EllipticK(Math.Sqrt(1 - k * k));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — the single-conductor standard is bit-identical to what it was before RP-2c
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The one structural change on the shipped path is that D4's longitudinal partition was
    /// lifted into a shared helper, so the coplanar builder could use it rather than copy it. This
    /// asserts it did not move a bit.</b>
    ///
    /// <para>The pre-RP-2c arithmetic is written out here as a NAMED REFERENCE, the way RP-2a's own
    /// gate 1 wrote out the pre-RP-2a incidence loop, and for the same reason: a committed golden of
    /// raw doubles would be a claim about this machine, while both sides of this comparison run in
    /// one process on one input. What it protects is every de-embedded number in the L8/L9
    /// acceptance set — R-prt-5 already asserts the standard's coordinates as an EQUALITY, and this
    /// says the equality is still to the last bit.</para>
    /// </summary>
    [Fact]
    public void Gate1_ASingleConductorStandard_IsBitIdenticalToThePreRp2cBuilder()
    {
        var slab = GroundedSlab.Fr4Starter;
        var (_, prt) = PlanarLineFixtures.MeshAndPorts(PlanarLineFixtures.Fr4Line(20e-3, 5e9));
        var port = prt[0];

        Assert.Null(port.CrossSection);            // a ground-referenced port takes no cross-section
        int k = PlanarCalibration.EndRunCellsFor(port, slab);

        foreach (double target in new[] { 3 * slab.HeightM, 8e-3, 17e-3 })
        {
            var built = PlanarCalibration.BuildLine(port, target, k);
            Assert.Null(built.ModePotential);      // and therefore no modal electrostatics
            Assert.Null(built.ModeWeight);
            Assert.False(built.IsCoplanar);

            var want = PreRp2cLongitudinal(port, target, k);
            Assert.Equal(want.Length, built.Mesh.GridX.Count);
            for (int i = 0; i < want.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(want[i]),
                             BitConverter.DoubleToInt64Bits(built.Mesh.GridX[i]));

            // The transverse half was never touched, and the whole cell list is checked rather than
            // the grid alone — a cell emitted in a different order would be a different mesh.
            Assert.Equal(port.TransverseLines.Count, built.Mesh.GridY.Count);
            for (int i = 0; i < port.TransverseLines.Count; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(port.TransverseLines[i]),
                             BitConverter.DoubleToInt64Bits(built.Mesh.GridY[i]));

            Assert.Equal((want.Length - 1) * (port.TransverseLines.Count - 1), built.Mesh.Cells.Count);
            _out.WriteLine($"target {target * 1e3:0.###} mm → {built.Mesh.GridX.Count - 1} cells long, " +
                           $"length {built.LengthM * 1e3:0.####} mm, N = {built.Mesh.Bases.Count}: " +
                           "every gridline bit-identical to the pre-RP-2c partition");
        }

        // L8d's own arithmetic, in its own order, copied from PlanarCalibration as it stood before
        // RP-2c lifted it into LongitudinalPartition.
        static double[] PreRp2cLongitudinal(PlanarPortResolution p, double targetLengthM, int endRunCells)
        {
            var sizes = new List<double>();
            double endLen = 0;
            for (int i = 0; i < endRunCells; i++) { sizes.Add(p.LongitudinalRunM[i]); endLen += p.LongitudinalRunM[i]; }

            double bulk    = p.BulkCellM;
            double covered = 2 * (endLen - p.LongitudinalRunM[0]);
            int    fill    = Math.Max(0, (int)Math.Ceiling((targetLengthM - covered) / bulk));
            fill = Math.Max(fill, 4 - 2 * endRunCells);

            for (int i = 0; i < fill; i++) sizes.Add(bulk);
            for (int i = endRunCells - 1; i >= 0; i--) sizes.Add(p.LongitudinalRunM[i]);

            var g = new double[sizes.Count + 1];
            for (int i = 0; i < sizes.Count; i++) g[i + 1] = g[i] + sizes[i];
            return g;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 2 — a de-embedded coplanar uniform section is EXACT at the calibration lengths
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Four equations fix four unknowns, so at the two lengths the calibration was solved from the
    /// de-embedded section must be matched to machine zero — and that is the measurement that says
    /// the standard IS the port's neighbourhood</b> (§3's gate 2).
    ///
    /// <para>It is not a tolerance question. If the standard's cross-section were the signal
    /// conductor alone — a microstrip standard for a coplanar port, which is what RP-2a refused —
    /// the two standards' error boxes would not be the same object as each other's, and |S₁₁| here
    /// would come back at the 1e-2 level rather than at 1e-15. L8d measured 8.5e-16 for the
    /// microstrip case; the coplanar pair lands in the same place.</para>
    /// </summary>
    [Theory]
    [InlineData(0.3e-3, 0.15e-3, 2.5e-3, 10e9, 40e-3, 20, 12)]
    [InlineData(0.3e-3, 0.15e-3, 2.5e-3, 10e9, 40e-3, 20, 24)]
    [InlineData(0.3e-3, 0.15e-3, 5.0e-3, 10e9, 40e-3, 20,  6)]
    [InlineData(0.6e-3, 0.10e-3, 5.0e-3, 10e9, 40e-3, 20, 12)]
    [InlineData(0.2e-3, 0.60e-3, 5.0e-3, 10e9, 35e-3, 20, 12)]
    public void Gate2_ADeembeddedCoplanarSectionIsExactAtTheCalibrationLengths(
        double w, double s, double h, double f, double len, int cpw, int across)
    {
        var problem = Cps(w, s, h, len, f);
        var report  = SurfaceMesher.Mesh(problem, Mesh(cpw, across));
        var ports   = PlanarPorts.ResolveAll(report.Mesh, CoplanarEnds(len, w, s));

        var xs = ports[0].CrossSection;
        Assert.NotNull(xs);
        Assert.Equal(2, xs.ConductorCount);

        var kernel = PlanarLineFixtures.Kernel(problem.Slab, f);
        var cal    = new PlanarPortCalibrator(ports[0], problem.Slab, f, f);
        var c      = cal.At(kernel, f);

        double worst = 0;
        foreach (var std in cal.Standards)
        {
            Assert.True(std.IsCoplanar, "a conductor-referenced port's standard must be a coplanar line");

            // The standard's own two ports are TWO-CUT ports at ONE station (R-rp2c-2), resolved
            // through PlanarPorts like any other — the skew check is the same one.
            foreach (var p in std.Ports) Assert.NotNull(p.Negative);
            Assert.Equal(std.Port1.ReferencePlaneM - std.Port1.ReferencePlaneM,
                         std.Port1.Negative!.ReferencePlaneM - std.Port1.ReferencePlaneM);

            var raw = new PlanarSolveContext(std.Mesh, std.Ports).RawScatteringAt(kernel, f);
            double s11 = PlanarDeembed.Apply(raw, [c.Box, c.Box])[0, 0].Magnitude;
            worst = Math.Max(worst, s11);

            _out.WriteLine($"  ℓ = {std.LengthM * 1e3,8:F4} mm, N = {std.Mesh.Bases.Count,4}, " +
                           $"cells = {std.Mesh.Cells.Count,4}: |S₁₁| de-embedded = {s11:E3}");
        }

        _out.WriteLine($"W={w * 1e3:0.##} S={s * 1e3:0.###} h={h * 1e3:0.#} cells/λ={cpw} across={across} " +
                       $"N={report.UnknownCount}: worst |S₁₁| = {worst:E3}, βΔℓ = {c.Gamma.ElectricalDegrees:F1}°");

        Assert.True(worst < 1e-12,
            $"at its own calibration length a coplanar section de-embedded to |S₁₁| = {worst:E3}, " +
            "not machine zero — the standard is not the port's neighbourhood");

        // And the a₂₂ sign was decided by information rather than by noise, as L8d's own gate asks.
        Assert.True(c.Box.RejectedResidual > 5 * c.Box.ConsistencyResidual,
            $"the two a₂₂ signs are nearly equally consistent ({c.Box.ConsistencyResidual:E2} vs " +
            $"{c.Box.RejectedResidual:E2})");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 3 — γ two ways, one of which shares no algebra with the calibration
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The two-line trace against the travelling-wave recurrence on the DUT's own current</b> —
    /// no T-matrix, no error box, no calibration standard, not even a second solve
    /// (<see cref="CurrentWaveOracle"/>). L8d recorded 2.5e-4 … 3.9e-3 over 2–10 GHz for microstrip;
    /// the number for the coplanar pair is printed rather than merely passed.
    /// </summary>
    [Theory]
    [InlineData(0.3e-3, 0.15e-3, 5.0e-3, 10e9, 40e-3, 20,  6)]
    [InlineData(0.3e-3, 0.15e-3, 5.0e-3, 10e9, 40e-3, 20, 12)]
    [InlineData(0.3e-3, 0.15e-3, 5.0e-3, 10e9, 40e-3, 20, 24)]
    public void Gate3_GammaTwoWays_TheTwoLineTraceAndTheTravellingWave(
        double w, double s, double h, double f, double len, int cpw, int across)
    {
        var problem = Cps(w, s, h, len, f);
        var report  = SurfaceMesher.Mesh(problem, Mesh(cpw, across));
        var ports   = PlanarPorts.ResolveAll(report.Mesh, CoplanarEnds(len, w, s));
        var kernel  = PlanarLineFixtures.Kernel(problem.Slab, f);

        var two  = new PlanarPortCalibrator(ports[0], problem.Slab, f, f).At(kernel, f);
        var sol  = new PlanarSolveContext(report.Mesh, ports).SolveAt(kernel, f);
        var wave = CurrentWaveOracle.Extract(report.Mesh, sol.Currents[0], ports[0]);

        double relB = Math.Abs(wave.Beta - two.Gamma.Beta) / wave.Beta;
        double k0   = 2 * Math.PI * f / EmConstants.C0;

        _out.WriteLine($"across={across} N={report.UnknownCount}: " +
                       $"β(two-line) = {two.Gamma.Beta:F3}, β(wave) = {wave.Beta:F3} " +
                       $"(fit residual {wave.ResidualRel:E2}) → Δβ/β = {relB:E3};  " +
                       $"β/k₀ = {two.Gamma.Beta / k0:F5} against 1 exactly, which is the KERNEL's " +
                       "own discretisation and not the calibration's");

        Assert.True(relB < 5e-3, $"β from the two routes differs by {relB:E3}");
        Assert.True(two.Gamma.Usable,
            $"βΔℓ = {two.Gamma.ElectricalDegrees:F1}° is outside TRL's usable interval");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 4 — the de-embedded coplanar line against the conformal-mapping closed form
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§3's gate 4: RP-2a's own oracle, now with the feed removed — which is the number a user
    /// actually reads.</b> Z_c = γ/(jωC_pul) against η₀·K(k)/K(k′).
    ///
    /// <para><b>Both halves of that ratio are printed, deliberately.</b> The medium is AIR, so β must
    /// be k₀ exactly and C_pul must be 1/(c·Z_c) exactly; the solve gets both a little low and the
    /// two errors PARTLY CANCEL in their quotient. Reporting only the quotient would make a 3.5%
    /// discretisation error look like a 1% agreement, so the ladder below prints β/k₀ and C_pul's
    /// own ratio beside it and the transverse refinement is what moves them.</para>
    /// </summary>
    [Theory]
    [InlineData(0.3e-3, 0.15e-3, 5.0e-3, 10e9, 40e-3, 20,  6)]
    [InlineData(0.3e-3, 0.15e-3, 5.0e-3, 10e9, 40e-3, 20, 12)]
    [InlineData(0.3e-3, 0.15e-3, 5.0e-3, 10e9, 40e-3, 20, 24)]
    public void Gate4_ADeembeddedCoplanarLine_MatchesTheConformalMappingClosedForm(
        double w, double s, double h, double f, double len, int cpw, int across)
    {
        var problem = Cps(w, s, h, len, f);
        var report  = SurfaceMesher.Mesh(problem, Mesh(cpw, across));
        var ports   = PlanarPorts.ResolveAll(report.Mesh, CoplanarEnds(len, w, s));
        var kernel  = PlanarLineFixtures.Kernel(problem.Slab, f);
        var c       = new PlanarPortCalibrator(ports[0], problem.Slab, f, f).At(kernel, f);

        double zcClosed = CpsZ0(w, s);
        double cClosed  = 1.0 / (EmConstants.C0 * zcClosed);
        double k0       = 2 * Math.PI * f / EmConstants.C0;

        _out.WriteLine(
            $"across={across} N={report.UnknownCount}:  Z_c = {c.Zc.Real:F2} Ω against the closed " +
            $"form's {zcClosed:F2} Ω → {c.Zc.Real / zcClosed:F4}×   " +
            $"[β/k₀ = {c.Gamma.Beta / k0:F4}, C_pul = {c.CPerMetre:E4} F/m = " +
            $"{c.CPerMetre / cClosed:F4}× the closed form's]");

        Assert.True(Math.Abs(c.Zc.Real / zcClosed - 1.0) < 0.08,
            $"the de-embedded coplanar Z_c is {c.Zc.Real / zcClosed:F4}× the closed form");

        // A lossless line in a homogeneous medium: Z_c is real and α is negligible beside β.
        Assert.True(Math.Abs(c.Zc.Imaginary) < 0.01 * Math.Abs(c.Zc.Real),
            $"Z_c came back at {c.Zc} on a lossless air line");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 5 — R-rp2c-4: the MIXED case is the ordinary case
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Port 1 returns through the plane, port 2 returns through the drawn strip beside it, one
    /// solve, one medium — and the run needs TWO differently-shaped standards.</b>
    ///
    /// <para>The cost is measured against the same geometry with both ports on the plane and
    /// reported rather than asserted: what makes it bearable is that <c>PlanarSolve</c> shares one
    /// DCIM fit across the DUT and every standard, because the fit depends on (slab, frequency)
    /// alone — and that stays true when the standards have different SHAPES, since a shape is not
    /// part of that key.</para>
    /// </summary>
    [Fact]
    public void Gate5_AMixedCalibrationRuns_AndItsCostIsReported()
    {
        const double w = 0.3e-3, s = 0.15e-3, h = 5e-3, f = 10e9, len = 40e-3;

        // The return strip runs beside the LAST third of the line only, so port 1 is an ordinary
        // microstrip port with a clean feed and port 2 is a coplanar one. A pair that ran the whole
        // length would put the return strip 150 µm from port 1's feed, and R-prt-3's clearance
        // warning would be right to fire: a microstrip standard is not that port's neighbourhood.
        var problem = PlanarLineFixtures.Problem(
            new GroundedSlab(h, new EmMaterial(1.0, 0.0)), f,
            PlanarLineFixtures.Rect(0,        0.5 * s,       len,  0.5 * s + w),
            PlanarLineFixtures.Rect(25e-3, -(0.5 * s + w),   len, -0.5 * s));
        var report  = SurfaceMesher.Mesh(problem, Mesh(20, 12));
        double yc   = 0.5 * s + 0.5 * w;

        PlanarPort plane1 = new(1, new EmPoint(0, yc), PlanarPortSide.MinX, 50.0);
        PlanarPort plane2 = new(2, new EmPoint(len, yc), PlanarPortSide.MaxX, 50.0);
        PlanarPort cop2   = new(2, new EmPoint(len, yc), PlanarPortSide.MaxX, 50.0,
                                Reference: PlanarPortReference.CoplanarGround,
                                NegativeLocation: new EmPoint(len, -yc));

        var mixed = PlanarPorts.ResolveAll(report.Mesh, [plane1, cop2]);
        var both  = PlanarPorts.ResolveAll(report.Mesh, [plane1, plane2]);

        Assert.Null(mixed[0].CrossSection);
        Assert.NotNull(mixed[1].CrossSection);
        Assert.False(PlanarPortCalibrator.SameCrossSection(
            mixed[0], mixed[1], PlanarCalibration.EndRunCellsFor(mixed[0], problem.Slab)));

        var swMixed = System.Diagnostics.Stopwatch.StartNew();
        var rMixed  = PlanarSolve.Run(problem, report.Mesh, mixed, [f]);
        swMixed.Stop();

        var swPlane = System.Diagnostics.Stopwatch.StartNew();
        var rPlane  = PlanarSolve.Run(problem, report.Mesh, both, [f]);
        swPlane.Stop();

        _out.WriteLine($"mixed : {rMixed.StandardCount} standard mesh(es), core fills " +
                       $"{rMixed.CoreFillCount}, {swMixed.Elapsed.TotalMilliseconds:F0} ms");
        _out.WriteLine($"plane : {rPlane.StandardCount} standard mesh(es), core fills " +
                       $"{rPlane.CoreFillCount}, {swPlane.Elapsed.TotalMilliseconds:F0} ms");
        _out.WriteLine($"DUT N = {report.UnknownCount};  cost ratio " +
                       $"{swMixed.Elapsed.TotalMilliseconds / Math.Max(swPlane.Elapsed.TotalMilliseconds, 1e-9):F2}×");
        foreach (var n in rMixed.Notes) _out.WriteLine("  note: " + n);

        // Two ports with two different references cannot share one calibration, so the mixed run
        // carries strictly more standard meshes than the all-plane one — that is the cost, and it is
        // the shape of it rather than a wall-clock number that is worth asserting.
        Assert.True(rMixed.StandardCount >= rPlane.StandardCount);

        var pt = rMixed.Points[0];
        Assert.Equal(2, pt.Calibrations.Count);
        for (int i = 0; i < 2; i++)
            _out.WriteLine($"  port {pt.Calibrations[i].PortNumber}: Z_c = {pt.Calibrations[i].Zc.Real:F2} Ω, " +
                           $"C_pul = {pt.Calibrations[i].CPerMetre:E4} F/m, " +
                           $"ε_eff = {pt.Calibrations[i].Gamma.EffectivePermittivity(f):F4}");

        // The two references really are two different lines: a coplanar pair's Z_c and a microstrip's
        // over the same slab are nowhere near each other, and a calibration that had quietly used one
        // standard for both would report one number twice.
        Assert.True(Math.Abs(pt.Calibrations[0].Zc.Real - pt.Calibrations[1].Zc.Real) > 1.0,
            "the two ports reported the same Z_c — they did not get their own standards");

        // ── R-rp2c-4's own question, measured on ONE port so nothing else differs ───────────────
        //
        // The same resolution, the same longitudinal partition, the same target length — built once
        // as the coplanar pair it is and once as the single conductor it would have been. That is
        // the whole of what a coplanar standard costs: the fill, the kernel and the shared DCIM fit
        // are the same objects, so the cost is UNKNOWNS, and this says how many.
        int kc = PlanarCalibration.EndRunCellsFor(mixed[1], problem.Slab);
        var coplanarStd  = PlanarCalibration.BuildLine(mixed[1], 15e-3, kc);
        var asMicrostrip = PlanarCalibration.BuildLine(mixed[1] with { CrossSection = null }, 15e-3, kc);

        double Solve(PlanarStandard std)
        {
            var kern = PlanarLineFixtures.Kernel(problem.Slab, f);
            var ctx  = new PlanarSolveContext(std.Mesh, std.Ports);
            ctx.RawScatteringAt(kern, f);                       // warm the cores
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            ctx.RawScatteringAt(kern, f);
            return sw2.Elapsed.TotalMilliseconds;
        }

        double msMs = Solve(asMicrostrip), cpMs = Solve(coplanarStd);
        _out.WriteLine(
            $"one port, one target length: the coplanar standard is N = {coplanarStd.Mesh.Bases.Count} " +
            $"({coplanarStd.Mesh.Cells.Count} cells) against the single-conductor standard's " +
            $"N = {asMicrostrip.Mesh.Bases.Count} ({asMicrostrip.Mesh.Cells.Count} cells) — " +
            $"{(double)coplanarStd.Mesh.Bases.Count / asMicrostrip.Mesh.Bases.Count:F2}× the unknowns, " +
            $"{cpMs:F1} ms against {msMs:F1} ms to fill and solve ({cpMs / msMs:F2}×)");

        Assert.True(coplanarStd.IsCoplanar);
        Assert.False(asMicrostrip.IsCoplanar);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 6 — the refusal RP-2a left behind is gone, and nothing else was un-refused with it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gate6a_AConductorReferencedEdgePort_IsNoLongerRefused()
    {
        const double w = 0.3e-3, s = 0.15e-3, h = 5e-3, f = 10e9, len = 40e-3;
        var report = SurfaceMesher.Mesh(Cps(w, s, h, len, f), Mesh(20, 6));

        Assert.True(PlanarPorts.TryResolve(report.Mesh, CoplanarEnds(len, w, s)[0],
                                           out var res, out string? why),
                    $"a conductor-referenced EDGE port was refused: {why}");
        Assert.NotNull(res!.Negative);
        Assert.NotNull(res.CrossSection);
        Assert.True(res.IsDeembeddable);
        _out.WriteLine(res.Describe());
    }

    /// <summary>An internal (via-to-plane) port still takes no reference — a different statement
    /// from RP-2a's edge refusal, and one that is about what the port IS.</summary>
    [Fact]
    public void Gate6b_AViaToPlanePortStillTakesNoReference()
    {
        const double w = 0.3e-3, s = 0.15e-3, h = 5e-3, f = 10e9, len = 40e-3;
        var report = SurfaceMesher.Mesh(Cps(w, s, h, len, f), Mesh(20, 6));
        double yc  = 0.5 * s + 0.5 * w;

        var port = new PlanarPort(1, new EmPoint(0.5 * len, yc), PlanarPortSide.MinX, 50.0,
                                  Reference: PlanarPortReference.CoplanarGround,
                                  Kind: PlanarPortKind.Internal,
                                  NegativeLocation: new EmPoint(0.5 * len, -yc));

        Assert.False(PlanarPorts.TryResolve(report.Mesh, port, out _, out string? why));
        _out.WriteLine(why!);
        Assert.Contains("negative terminal is the ground plane by construction", why);
    }

    /// <summary>RP-2a's pair checks are about the PAIR, not about the kind, so they must still fire
    /// now that an edge port can reach them. A skewed edge pair is a port plus a length of line.</summary>
    [Fact]
    public void Gate6c_TheRp2aPairChecksStillFire_OnAnEdgePort()
    {
        const double w = 0.3e-3, s = 0.15e-3, h = 5e-3, f = 10e9, len = 40e-3;
        // One conductor 6 mm shorter than the other, so the two end faces are at different stations.
        var problem = PlanarLineFixtures.Problem(
            new GroundedSlab(h, new EmMaterial(1.0, 0.0)), f,
            PlanarLineFixtures.Rect(0,      0.5 * s,       len,  0.5 * s + w),
            PlanarLineFixtures.Rect(6e-3, -(0.5 * s + w),  len, -0.5 * s));
        var report = SurfaceMesher.Mesh(problem, Mesh(20, 6));
        double yc  = 0.5 * s + 0.5 * w;

        var skewed = new PlanarPort(1, new EmPoint(0, yc), PlanarPortSide.MinX, 50.0,
                                    Reference: PlanarPortReference.CoplanarGround,
                                    NegativeLocation: new EmPoint(0, -yc));

        Assert.False(PlanarPorts.TryResolve(report.Mesh, skewed, out _, out string? why));
        _out.WriteLine(why!);
        Assert.Contains("different stations", why);
        Assert.Contains("port plus that length of line", why);

        // And the same-conductor check: both points on the signal strip.
        var sameCond = new PlanarPort(1, new EmPoint(0, yc), PlanarPortSide.MinX, 50.0,
                                      Reference: PlanarPortReference.CoplanarGround,
                                      NegativeLocation: new EmPoint(0, yc + 0.4 * w));
        Assert.False(PlanarPorts.TryResolve(report.Mesh, sameCond, out _, out string? why2));
        _out.WriteLine(why2!);
    }

    /// <summary>
    /// <b>An EDGE pair whose two cuts are on DIFFERENT levels is refused, and it is not the same
    /// statement RP-2a's level check makes.</b>
    ///
    /// <para>RP-2a permits a level-spanning pair when both levels were STATED, and for an internal
    /// delta gap that is right: it has no standard. An edge port does, and a standard is a uniform
    /// line on ONE level (D3) — the two-line algebra models the section between the planes as a
    /// matched uniform line, and a level change in the middle of it is a discontinuity in the thing
    /// being assumed. Building it on the signal's level anyway would put the return conductor beside
    /// the signal instead of under it: a plausible s-parameter set for a line nobody has.</para>
    /// </summary>
    [Fact]
    public void Gate6e_AnEdgePairSpanningTwoLevels_IsRefusedBecauseAStandardIsOneLevel()
    {
        const double f = 10e9, len = 400e-6;
        var stack = LayerStacks.MmicTwoLevel;
        var problem = new PlanarProblem(
        [
            new PlanarConductorLayer("M1", [PlanarLineFixtures.Rect(0, 0, len, 100e-6)],
                                     4.1e7, 2e-6, stack.InterfaceZ[1]),
            new PlanarConductorLayer("M2", [PlanarLineFixtures.Rect(0, 0, len, 100e-6)],
                                     4.1e7, 3e-6, stack.TopZ),
        ], GroundedSlab.GaAsStarter, f, null, stack, []);

        var report = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);

        // Both levels are stated, which is exactly what RP-2a's own check allows.
        var port = new PlanarPort(1, new EmPoint(0, 50e-6), PlanarPortSide.MinX, 50.0,
                                  LayerIndex: 0,
                                  Reference: PlanarPortReference.SecondConductor,
                                  NegativeLocation: new EmPoint(0, 50e-6),
                                  NegativeLayerIndex: 1);

        Assert.False(PlanarPorts.TryResolve(report.Mesh, port, out _, out string? why));
        _out.WriteLine(why!);
        Assert.Contains("two cuts are on different levels", why);
        Assert.Contains("uniform line on ONE conductor level", why);
        Assert.Contains("internal delta gap", why);       // the remedy that works today
    }

    /// <summary>
    /// <b>A THIRD conductor at the reference plane is refused by name at calibration setup.</b> The
    /// coplanar standard drives the port's PAIR — signal at +½ V, return at −½ V — and a CPW's far
    /// ground strip has no stated potential; the two reasonable readings (bonded to the return, or
    /// floating at zero net charge) are far apart, so it is not chosen silently.
    /// </summary>
    [Fact]
    public void Gate6d_AThirdConductorAtThePlane_IsRefusedByName()
    {
        const double w = 0.3e-3, s = 0.15e-3, h = 5e-3, f = 10e9, len = 40e-3;
        var problem = PlanarLineFixtures.Problem(
            new GroundedSlab(h, new EmMaterial(1.0, 0.0)), f,
            PlanarLineFixtures.Rect(0,  0.5 * s,           len,  0.5 * s + w),   // signal
            PlanarLineFixtures.Rect(0, -(0.5 * s + w),     len, -0.5 * s),       // named return
            PlanarLineFixtures.Rect(0,  0.5 * s + w + s,   len,  0.5 * s + 2 * w + s)); // the far ground
        var report = SurfaceMesher.Mesh(problem, Mesh(20, 6));
        double yc  = 0.5 * s + 0.5 * w;

        var ports = PlanarPorts.ResolveAll(report.Mesh,
        [
            new PlanarPort(1, new EmPoint(0, yc), PlanarPortSide.MinX, 50.0,
                           Reference: PlanarPortReference.CoplanarGround,
                           NegativeLocation: new EmPoint(0, -yc)),
            new PlanarPort(2, new EmPoint(len, yc), PlanarPortSide.MaxX, 50.0,
                           Reference: PlanarPortReference.CoplanarGround,
                           NegativeLocation: new EmPoint(len, -yc)),
        ]);
        Assert.Equal(3, ports[0].CrossSection!.ConductorCount);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanarSolve.Run(problem, report.Mesh, ports, [f]));
        _out.WriteLine(ex.Message);
        Assert.Contains("3 separate conductors cross its reference plane", ex.Message);
        Assert.Contains("Join the ground strips", ex.Message);

        // …and the raw solve is still available, which is what the refusal offers.
        var raw = PlanarSolve.Run(problem, report.Mesh, ports, [f],
                                  PlanarSolveSettings.Default with { Deembed = false });
        Assert.Single(raw.Points);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE FLOOR UNDER GATE 2, AND THE CONTROL THAT SAYS IT IS NOT RP-2c's
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>On some meshes the de-embedded |S₁₁| of a standard sits at ~1e-6 instead of ~1e-15, and
    /// the cause is a pre-existing mirror asymmetry in the RAW solve rather than anything about the
    /// coplanar standard.</b>
    ///
    /// <para>A calibration standard is mirror-symmetric by construction, so its raw S₁₁ and S₂₂ must
    /// be equal; where they are not, <see cref="PlanarDeembed.SolveErrorBox"/> averages them (which
    /// is the right use of a known symmetry) and the difference comes back as exactly the de-embedded
    /// |S₁₁| — the two numbers track each other one for one across every fixture measured.</para>
    ///
    /// <para><b>The control is what settles the attribution:</b> the same mesh driven by ordinary
    /// GROUND-REFERENCED ports — the port every L8/L9 number uses, which predates RP-2a entirely —
    /// shows the same |S₁₁ − S₂₂| to within a couple of per cent. So it is a property of the fill and
    /// the solve on that geometry, not of the two-cut incidence column and not of the coplanar
    /// cross-section. It is asserted here rather than left in prose, because a note nobody can run
    /// is a note that stops being true.</para>
    ///
    /// <para>Not chased further, deliberately: it is bounded, it is off RP-2c's path, and P5 already
    /// records that per-entry agreement at 1e-12 is unattainable in this fill for absolute-coordinate
    /// reasons. What it costs here is that gate 2's fixtures are chosen where it is absent, and that
    /// choice is stated rather than hidden.</para>
    /// </summary>
    [Theory]
    [InlineData(40e-3, 12)]
    [InlineData(30e-3, 12)]
    [InlineData(30e-3, 24)]
    public void TheGate2Floor_IsARawMirrorAsymmetryTheGroundReferencedPortShowsToo(double len, int across)
    {
        const double w = 0.3e-3, s = 0.15e-3, h = 5e-3, f = 10e9;
        var problem = Cps(w, s, h, len, f);
        var report  = SurfaceMesher.Mesh(problem, Mesh(20, across));
        var kernel  = PlanarLineFixtures.Kernel(problem.Slab, f);
        double yc   = 0.5 * s + 0.5 * w;

        var twoCut = PlanarPorts.ResolveAll(report.Mesh, CoplanarEnds(len, w, s));
        var ground = PlanarPorts.ResolveAll(report.Mesh,
        [
            new PlanarPort(1, new EmPoint(0, yc), PlanarPortSide.MinX, 50.0),
            new PlanarPort(2, new EmPoint(len, yc), PlanarPortSide.MaxX, 50.0),
        ]);

        double AsymOf(IReadOnlyList<PlanarPortResolution> p)
        {
            var raw = new PlanarSolveContext(report.Mesh, p).RawScatteringAt(kernel, f);
            return (raw[0, 0] - raw[1, 1]).Magnitude;
        }

        double aTwoCut = AsymOf(twoCut), aGround = AsymOf(ground);
        _out.WriteLine($"len={len * 1e3:0.#} mm across={across} N={report.UnknownCount}: " +
                       $"|S₁₁ − S₂₂| = {aTwoCut:E3} with two-cut ports, {aGround:E3} with " +
                       $"ground-referenced ports on the SAME mesh → ratio {aTwoCut / aGround:F3}");

        // Both are mirror-symmetric structures; whatever the floor is, the two ports see the same one.
        Assert.True(aTwoCut / aGround is > 0.5 and < 2.0,
            $"the two-cut port's mirror asymmetry ({aTwoCut:E3}) is not the ground-referenced port's " +
            $"({aGround:E3}) — then it WOULD be RP-2c's to explain");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5's third report item — the direct port-to-port coupling, with coplanar grounds present
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>L8d's own finding, asked again of a coplanar pair.</b> For microstrip the de-embedded
    /// |S₁₁| of a uniform section is exact at the two calibration lengths and drifts away from them —
    /// 3.9e-4 at 2 GHz on the FR-4 hero, rising as f², and NOT monotone in the standard's length,
    /// which is the signature of direct radiative and surface-wave coupling rather than of an
    /// evanescent tail. The question §5 asks is whether the grounds beside the line change that.
    ///
    /// <para>Printed rather than gated, except for the exactness at the calibration's own length,
    /// which IS the algebra.</para>
    /// </summary>
    [Fact]
    public void CouplingResidual_TheDriftAwayFromTheCalibrationLengths_WithCoplanarGrounds()
    {
        const double w = 0.3e-3, s = 0.15e-3, h = 2.5e-3, len = 40e-3;

        foreach (double f in new[] { 5e9, 10e9, 20e9 })
        {
            var problem = Cps(w, s, h, len, f);
            var report  = SurfaceMesher.Mesh(problem, Mesh(20, 12));
            var ports   = PlanarPorts.ResolveAll(report.Mesh, CoplanarEnds(len, w, s));
            var kernel  = PlanarLineFixtures.Kernel(problem.Slab, f);
            var cal     = new PlanarPortCalibrator(ports[0], problem.Slab, f, f);
            var c       = cal.At(kernel, f);
            int k       = PlanarCalibration.EndRunCellsFor(ports[0], problem.Slab);

            // The CONTROLLED counterpart: the signal strip alone on the SAME slab at the SAME mesh,
            // driven by ordinary ground-referenced ports. Same medium, same cell size, same
            // frequency — so the only difference is whether the return is drawn metal beside the
            // line or the plane 5 mm below it, which is exactly the question §5 asks.
            var msProblem = PlanarLineFixtures.Line(problem.Slab, w, len, f);
            var msReport  = SurfaceMesher.Mesh(msProblem, Mesh(20, 12));
            var msPorts   = PlanarPorts.ResolveAll(msReport.Mesh, PlanarLineFixtures.EndPorts(msProblem));
            var msCal     = new PlanarPortCalibrator(msPorts[0], problem.Slab, f, f);
            var msC       = msCal.At(kernel, f);
            int msK       = PlanarCalibration.EndRunCellsFor(msPorts[0], problem.Slab);

            _out.WriteLine($"{f / 1e9,5:F1} GHz  (h/λ₀ = {h * f / EmConstants.C0:F4}, " +
                           $"βΔℓ = {c.Gamma.ElectricalDegrees:F1}°)   " +
                           $"[coplanar pair | microstrip control]");
            double atOne = double.NaN;
            foreach (double mult in new[] { 1.0, 1.5, 2.0, 2.5 })
            {
                var st  = PlanarCalibration.BuildLine(ports[0], cal.Standards[0].LengthM * mult, k);
                var raw = new PlanarSolveContext(st.Mesh, st.Ports).RawScatteringAt(kernel, f);
                var sd  = PlanarDeembed.Apply(raw, [c.Box, c.Box]);
                if (double.IsNaN(atOne)) atOne = sd[0, 0].Magnitude;

                var mst  = PlanarCalibration.BuildLine(msPorts[0], msCal.Standards[0].LengthM * mult, msK);
                var mraw = new PlanarSolveContext(mst.Mesh, mst.Ports).RawScatteringAt(kernel, f);
                var msd  = PlanarDeembed.Apply(mraw, [msC.Box, msC.Box]);

                _out.WriteLine($"    ℓ = {st.LengthM * 1e3,7:F3} mm (βℓ = {c.Gamma.Beta * st.LengthM,6:F2} rad): " +
                               $"|S₁₁| = {sd[0, 0].Magnitude:E3}   |   " +
                               $"ℓ = {mst.LengthM * 1e3,7:F3} mm: |S₁₁| = {msd[0, 0].Magnitude:E3}");
            }

            Assert.True(atOne < 1e-12,
                $"at the standard's own length the de-embedding should be exact, not {atOne:E3}");
        }
    }
}
