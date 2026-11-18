// PCAL4 — which ports become a CALIBRATION GROUP, what is declined and by what name, and what the
// group's standard is made of. `docs/sonnet-briefs/brief-portcal-4-modal-error-box.md`.
//
// The geometry is the series' own coupled pair, driven at both ends of both conductors — the case
// PCAL1 measured at 22 dB out in S₂₁ at 1 GHz and PCAL2 turned into a refusal. What is gated here is
// the DECISION and the CONSTRUCTION; the algebra is `ModalLineCalibrationTests`, on synthetic data
// where the answer is known, and the accuracy measurement against the cross-section oracle lives in
// `src/Engine/Mom/RESOLVED.md` because it needs seven frequencies of two kernels and costs minutes.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarCalibrationGroupTests(ITestOutputHelper output)
{
    private static readonly GroundedSlab Slab = new(0.9e-3, new EmMaterial(4.4, 0.02));
    private const double W = 254e-6, Len = 3.83e-3, F = 5e9;

    /// <summary>N conductors <paramref name="heights"/> × h apart, each with a port at both ends
    /// unless <paramref name="undriven"/> names it. <paramref name="shortenLast"/> pulls the last
    /// conductor's far end in, so it ends inside the standard's own run.</summary>
    private static (PlanarMesh Mesh, IReadOnlyList<PlanarPortResolution> Ports)
        Group(double heights, int conductors = 2, int undriven = -1, double? lastLength = null,
              double? lastOffset = null)
    {
        double gap = heights * Slab.HeightM;
        var polys = new List<PlanarPolygon>();
        var ports = new List<PlanarPort>();
        double y = 0;
        int n = 0;
        for (int k = 0; k < conductors; k++)
        {
            double x0 = k == conductors - 1 && lastOffset is { } o ? o : 0;
            double x1 = k == conductors - 1 && lastLength is { } l ? x0 + l : Len;
            polys.Add(PlanarLineFixtures.Rect(x0, y, x1, y + W));
            if (k != undriven)
            {
                ports.Add(new PlanarPort(++n, new EmPoint(x0, y + 0.5 * W), PlanarPortSide.MinX, 50.0));
                ports.Add(new PlanarPort(++n, new EmPoint(x1, y + 0.5 * W), PlanarPortSide.MaxX, 50.0));
            }
            y += W + gap;
        }

        var problem = PlanarLineFixtures.Problem(Slab, F, [.. polys]);
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        return (mesh, PlanarPorts.ResolveAll(mesh, ports));
    }

    private static PlanarPortGroupProfile? Form(
        PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports, out string? declined,
        double drivenHeights = 5.0, int maxSize = 3, int at = 0)
        => PlanarPorts.TryFormCalibrationGroup(
            mesh, ports[at], ports,
            PlanarCalibration.EndRunCellsFor(ports[at], Slab),
            drivenHeights * Slab.HeightM, maxSize, PlanarConductors.Of(mesh), out declined);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pcal4-1 — the group forms, and a group of one is not a group
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TwoDrivenConductorsAtOnePlaneFormAGroup_NamingBothPorts()
    {
        var (mesh, ports) = Group(0.27);
        var g = Form(mesh, ports, out string? declined);

        Assert.Null(declined);
        Assert.NotNull(g);
        Assert.Equal(2, g!.ConductorCount);
        Assert.Equal([1, 3], g.PortNumbers);                  // both low-x ports, in y order
        Assert.Equal(0.27 * Slab.HeightM, g.NearestM, 8);

        output.WriteLine(g.Describe());
        output.WriteLine($"profile {g.SpanLoM * 1e6:F1}–{g.SpanHiM * 1e6:F1} µm, " +
                         $"{g.ConductorOf.Count} interval(s)");

        // Every interval is labelled: the port's own conductor, the gap, and the neighbour's.
        Assert.Contains(-1, g.ConductorOf);
        Assert.Contains(0, g.ConductorOf);
        Assert.Contains(1, g.ConductorOf);
        Assert.Equal(0, g.IndexOfPort(1));
        Assert.Equal(1, g.IndexOfPort(3));
    }

    /// <summary><b>R-pcal4-1's "a group of one is today's case and must remain bit-identical".</b> A
    /// clear feed returns null with NO reason, which is how the caller tells "nothing to group" from
    /// "could not group", and never reaches any of the new code at all.</summary>
    [Fact]
    public void AClearFeedFormsNoGroup_AndSaysNothing()
    {
        var (mesh, ports) = Group(7.0);
        Assert.Null(Form(mesh, ports, out string? declined));
        Assert.Null(declined);
    }

    /// <summary>The two ends of one coupled pair are two DIFFERENT groups, each naming its own
    /// ports — the plane is what a group is cut at.</summary>
    [Fact]
    public void EachEndOfThePairIsItsOwnGroup()
    {
        var (mesh, ports) = Group(0.27);
        var lo = Form(mesh, ports, out _, at: 0)!;
        var hi = Form(mesh, ports, out _, at: 1)!;

        Assert.Equal([1, 3], lo.PortNumbers);
        Assert.Equal([2, 4], hi.PortNumbers);

        // Same cross-section, so one standard serves both — asserted on the profile, which is what
        // PlanarPortCalibrator.SameCrossSection compares.
        Assert.Equal(lo.ConductorOf, hi.ConductorOf);
        for (int i = 0; i < lo.Lines.Count; i++) Assert.Equal(lo.Lines[i], hi.Lines[i], 15);
    }

    [Fact]
    public void ThreeCoupledConductorsFormOneGroupOfThree()
    {
        var (mesh, ports) = Group(0.27, conductors: 3);
        var g = Form(mesh, ports, out string? declined);
        Assert.Null(declined);
        Assert.Equal(3, g!.ConductorCount);
        Assert.Equal([1, 3, 5], g.PortNumbers);
        output.WriteLine(g.Describe());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pcal4-6 — what is declined, and that every decline is BY NAME
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>One driven conductor and one floating one cannot share a standard.</b> PCAL3's widened
    /// neighbour is held at zero NET charge and PCAL4's group conductors are all driven; a standard
    /// carrying both would measure a reference impedance belonging to neither structure.
    /// </summary>
    [Fact]
    public void AnUndrivenNeighbourBesideADrivenOneIsDeclinedByName()
    {
        var (mesh, ports) = Group(0.27, conductors: 3, undriven: 2);
        Assert.Null(Form(mesh, ports, out string? declined));
        output.WriteLine(declined ?? "(no reason)");
        Assert.NotNull(declined);
        Assert.Contains("carries NO port", declined!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A neighbour whose port is at a different station is declined</b> — and this is the
    /// commonest shape of the problem on a real board, not a corner case. A group is one standard
    /// cut at ONE plane; two ports that do not share a plane do not share the modes at it.
    /// </summary>
    [Fact]
    public void ANeighbourWhosePortIsElsewhereIsDeclinedByName()
    {
        var (mesh, ports) = Group(0.27, lastLength: 2.6e-3, lastOffset: 0.6e-3);
        Assert.Null(Form(mesh, ports, out string? declined, at: 2));
        output.WriteLine(declined ?? "(no reason)");
        Assert.NotNull(declined);
        Assert.Contains("reference plane", declined!, StringComparison.Ordinal);
    }

    /// <summary><b>R-pcal4-7's cap is a REFUSAL, not a silent truncation</b> — a group truncated to
    /// fit would calibrate against a cross-section that is not the port's.</summary>
    [Fact]
    public void AGroupLargerThanTheCapIsDeclinedByName()
    {
        var (mesh, ports) = Group(0.27, conductors: 3);
        Assert.Null(Form(mesh, ports, out string? declined, maxSize: 2));
        output.WriteLine(declined ?? "(no reason)");
        Assert.NotNull(declined);
        Assert.Contains("calibration group", declined!, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D4 — the group's standard is built from the DUT's OWN mesh, and is a 2N-port
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The transverse gridlines are the DUT's own, verbatim; the longitudinal end run is the port's
    /// own cell run at both ends; a cell exists only where the profile says metal; and the standard
    /// carries 2N ports, conductor k at the low end followed by conductor k at the high end —
    /// which is the block split every piece of the modal algebra assumes.
    /// </summary>
    [Fact]
    public void TheGroupStandardsCoordinatesAreTheDutsOwn_AndItIsA2NPort()
    {
        var (mesh, ports) = Group(0.27);
        var g = Form(mesh, ports, out _)!;
        var port = ports[0] with { Group = g };

        int k = PlanarCalibration.EndRunCellsFor(port, Slab);
        var std = PlanarCalibration.BuildLine(port, 3.0 * Slab.HeightM, k);

        Assert.True(std.IsGroup);
        Assert.Equal(2, std.ConductorCount);
        Assert.Equal(4, std.Ports.Count);
        Assert.NotNull(std.ConductorOfCell);

        Assert.Equal(g.Lines.Count, std.Mesh.GridY.Count);
        for (int i = 0; i < g.Lines.Count; i++)
        {
            Assert.Equal(g.Lines[i], std.Mesh.GridY[i], 15);
            Assert.Contains(mesh.GridY, gy => gy == g.Lines[i]);
        }
        for (int i = 0; i < k; i++)
        {
            Assert.Equal(port.LongitudinalRunM[i], std.Mesh.GridX[i + 1] - std.Mesh.GridX[i], 15);
            Assert.Equal(port.LongitudinalRunM[i], std.Mesh.GridX[^(i + 1)] - std.Mesh.GridX[^(i + 2)], 15);
        }

        int metal = g.ConductorOf.Count(c => c >= 0);
        Assert.Equal(metal * (std.Mesh.GridX.Count - 1), std.Mesh.Cells.Count);

        // Conductor k's two ports face opposite ways and sit on the same transverse coordinate.
        Assert.Equal(PlanarPortSide.MinX, std.Ports[0].Side);
        Assert.Equal(PlanarPortSide.MinX, std.Ports[1].Side);
        Assert.Equal(PlanarPortSide.MaxX, std.Ports[2].Side);
        Assert.Equal(PlanarPortSide.MaxX, std.Ports[3].Side);

        // Every cell is labelled with a conductor, and both conductors are present.
        Assert.All(std.ConductorOfCell!, c => Assert.InRange(c, 0, 1));
        Assert.Contains(0, std.ConductorOfCell!);
        Assert.Contains(1, std.ConductorOfCell!);

        output.WriteLine($"group standard: N = {std.Mesh.Bases.Count}, cells = {std.Mesh.Cells.Count}, " +
                         $"{std.Ports.Count} ports, length {std.LengthM * 1e3:F3} mm");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pcal4-2 / R-pcal4-6 — the modes have to be SEPARABLE, and the question is asked at setup
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The mode separation is a real quantity with a real value, and it is asked of the
    /// ELECTROSTATICS before a single frequency has been solved.</b> That is what lets a group that
    /// cannot be calibrated be refused at setup rather than in the middle of a sweep, and it is the
    /// one number R-pcal4-2 asks to be reported: at zero separation the cascade's eigenvalues
    /// coincide, its eigenvectors are a plane rather than two lines, and no arithmetic recovers
    /// which line is which mode.
    /// </summary>
    [Fact]
    public void TheModeSeparationIsMeasuredBeforeAnySolve_AndGrowsWithFrequency()
    {
        var (mesh, ports) = Group(0.27);
        var g = Form(mesh, ports, out _)!;
        var port = ports[0] with { Group = g };

        var cal = new PlanarPortCalibrator(port, Slab, 1e9, 7e9);
        Assert.True(cal.IsGroup);

        double lo = cal.QuasiStaticModeSeparationDegrees(1e9);
        double hi = cal.QuasiStaticModeSeparationDegrees(7e9);
        output.WriteLine($"mode separation over the selected Δℓ: {lo:F3}° at 1 GHz, {hi:F3}° at 7 GHz");

        Assert.True(lo > 0, "the two modes of a coupled pair are not distinguishable at all");
        Assert.True(hi > lo, "the separation in electrical length did not grow with frequency");

        // The modal medium is the standards' own electrostatics, and its quantities are physical:
        // two distinct velocities, and Tvᵀ[C]Tv diagonal to the loss's own coupling.
        var medium = cal.GroupMedium();
        Assert.Equal(2, medium.ModeCount);
        Assert.NotEqual(medium.Lambda[0], medium.Lambda[1]);
        output.WriteLine($"ε_eff(quasi-static) = {medium.Lambda[0] * 9e16:F3} / " +
                         $"{medium.Lambda[1] * 9e16:F3}; mode-coupling residual " +
                         $"{medium.ModeCouplingResidual:E2}");
        Assert.True(medium.ModeCouplingResidual < 1e-6);
    }

    /// <summary>
    /// <b>A group whose modes are closer than the floor is REFUSED by name, and it falls through to
    /// PCAL2's own refusal</b> — whose remedy (separate the feeds) is the remedy here too. A
    /// partially-correct modal de-embedding that publishes is the failure this whole series exists
    /// to remove, so this is not a note.
    /// </summary>
    [Fact]
    public void ModesTooCloseToSeparateAreRefusedAtSetup_ByName()
    {
        var (mesh, ports) = Group(0.27);
        var g = Form(mesh, ports, out _)!;
        var port = ports[0] with { Group = g };
        var cal = new PlanarPortCalibrator(port, Slab, 1e9, 7e9);

        double actual = cal.QuasiStaticModeSeparationDegrees(1e9);
        var strict = PlanarCalibrationSettings.Default with
        {
            ModeSeparationFloorDegrees = actual * 2.0,
        };

        var ex = Assert.Throws<PlanarFeedClearanceRefusedException>(
            () => PlanarSolve.GuardModeSeparation(cal, port, [1e9], strict, v => $"{v * 1e6:F0} µm"));
        output.WriteLine(ex.Message);
        Assert.Contains("not separable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Separate the feeds", ex.Message, StringComparison.Ordinal);

        // …and the shipped floor lets this fixture through, which is the other half of the claim.
        PlanarSolve.GuardModeSeparation(cal, port, [1e9], PlanarCalibrationSettings.Default,
                                        v => $"{v * 1e6:F0} µm");
    }

    /// <summary>
    /// <b>Members of one group share ONE standard, and two groups of different cross-sections do
    /// not.</b> The first half is what stops an asymmetric pair building and solving the identical
    /// 2N-port mesh once per port; the second is RP-2c's own rule, one conductor further out.
    /// </summary>
    [Fact]
    public void OneGroupIsOneStandard_AndTwoCrossSectionsAreTwo()
    {
        var (mesh, ports) = Group(0.27);
        var lo = Form(mesh, ports, out _, at: 0)!;
        var a = ports[0] with { Group = lo };
        var b = ports[2] with { Group = lo };
        int k = PlanarCalibration.EndRunCellsFor(a, Slab);
        Assert.True(PlanarPortCalibrator.SameCrossSection(a, b, k),
                    "two members of one group did not share a standard");

        var (wideMesh, widePorts) = Group(1.2);
        var far = Form(wideMesh, widePorts, out _, drivenHeights: 5.0)!;
        var c = widePorts[0] with { Group = far };
        Assert.False(PlanarPortCalibrator.SameCrossSection(a, c, k),
                     "two different group cross-sections shared a standard");
    }
}
