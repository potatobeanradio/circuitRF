using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// The <see cref="IEmKernel"/> seam and <b>R-mom-17</b>: <c>CanSolve</c> returns a <i>specific</i>
/// reason and it is the only place a refusal is worded. §10.3.3's requirement — <i>"This geometry
/// has a bend at (x, y); the quasi-static solver handles uniform cross-sections only. Full-wave
/// analysis of discontinuities arrives in L8"</i> — is the difference between v1 reading as bounded
/// and reading as broken, so every refusal here is asserted to name the specific feature <b>and</b>
/// where the capability arrives, not merely to be non-empty.
/// </summary>
public class KernelSeamTests
{
    private static readonly QuasiStaticKernel Kernel = new();

    private static EmProblem Good() => EmProblemBuilders.Fr4Microstrip(2.9e-3);

    private static string Refusal(EmProblem p)
    {
        var s = Kernel.CanSolve(p);
        Assert.False(s.Ok, "expected this problem to be refused");
        Assert.False(string.IsNullOrWhiteSpace(s.Reason));
        return s.Reason!;
    }

    [Fact]
    public void TheKernelDeclaresWhatItIs()
    {
        Assert.Equal(EmCapabilities.UniformCrossSection, Kernel.Capabilities);
        Assert.Contains("quasi-static", Kernel.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(Kernel.CanSolve(Good()).Ok);
        Assert.Null(Kernel.CanSolve(Good()).Reason);
    }

    /// <summary>R-mom-3: horizontal, laterally infinite interfaces are the 2.5D premise.</summary>
    [Fact]
    public void ANonTilingRegionStack_IsRefusedByNamingTheGapAndWhereTheCapabilityArrives()
    {
        var p = Good();
        var q = p with
        {
            Regions =
            [
                new EmDielectricRegion(double.NegativeInfinity, 1.5e-3, new EmMaterial(4.4)),
                new EmDielectricRegion(1.6e-3, double.PositiveInfinity, EmMaterial.Air),
            ],
        };
        string r = Refusal(q);
        Assert.Contains("gap", r, StringComparison.Ordinal);
        Assert.Contains("0.0015", r, StringComparison.Ordinal);
        Assert.Contains("vertical or sloped", r, StringComparison.Ordinal);
        // L8e/D6: this refusal used to point at L8 and was MISLEADING the moment kernel B shipped —
        // B is ONE grounded slab with ONE conductor level, so it cannot model a sloped dielectric
        // boundary either. Re-pointed at L9, by name.
        // L9e/M4 — UPDATED, NOT LOOSENED. Both halves of this refusal were false after L9: the
        // general stack HAS arrived, and it still does not help, because a sloped boundary is
        // outside the 2.5D premise both kernels share rather than behind a schedule.
        Assert.Contains("3-D formulation", r, StringComparison.Ordinal);
        Assert.DoesNotContain("arrives at L9", r, StringComparison.Ordinal);
        Assert.DoesNotContain("arrives with the full-wave kernel at L8", r, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlappingRegionStack_IsRefusedAsAnOverlap()
    {
        var p = Good();
        var q = p with
        {
            Regions =
            [
                new EmDielectricRegion(double.NegativeInfinity, 1.7e-3, new EmMaterial(4.4)),
                new EmDielectricRegion(1.6e-3, double.PositiveInfinity, EmMaterial.Air),
            ],
        };
        Assert.Contains("overlap", Refusal(q), StringComparison.Ordinal);
    }

    /// <summary>
    /// R-mom-4: a zero-thickness sheet has no interior for Wheeler's rule to recede into, so its
    /// conductor loss would be <i>undefined</i> rather than merely approximate — and the refusal
    /// has to say that, not just "invalid geometry".
    /// </summary>
    [Fact]
    public void AZeroThicknessSheet_IsRefusedWithTheWheelerReason()
    {
        var p = Good();
        var flat = new EmConductor("strip",
            [new EmPoint(-1e-3, 1.6e-3), new EmPoint(1e-3, 1.6e-3), new EmPoint(-1e-3, 1.6e-3)],
            EmProblemBuilders.CopperSigma);
        string r = Refusal(p with { Conductors = [flat] });

        Assert.Contains("'strip'", r, StringComparison.Ordinal);
        Assert.Contains("zero-thickness sheet", r, StringComparison.Ordinal);
        Assert.Contains("Wheeler", r, StringComparison.Ordinal);
        Assert.Contains("finite thickness", r, StringComparison.Ordinal);
    }

    [Fact]
    public void ATwoVertexConductor_IsRefusedAsNotAPolygon()
    {
        var p = Good();
        var line = new EmConductor("strip",
            [new EmPoint(-1e-3, 1.6e-3), new EmPoint(1e-3, 1.6e-3)], EmProblemBuilders.CopperSigma);
        string r = Refusal(p with { Conductors = [line] });
        Assert.Contains("'strip'", r, StringComparison.Ordinal);
        Assert.Contains("2 vertices", r, StringComparison.Ordinal);
        Assert.Contains("closed polygon", r, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelfIntersectingOutline_IsRefusedByNamingTheCrossingEdges()
    {
        var p = Good();
        // A bow-tie: the two diagonals cross.
        var bowtie = new EmConductor("strip",
        [
            new EmPoint(-1e-3, 1.60e-3), new EmPoint(1e-3, 1.64e-3),
            new EmPoint(-1e-3, 1.64e-3), new EmPoint(1e-3, 1.60e-3),
        ], EmProblemBuilders.CopperSigma);
        string r = Refusal(p with { Conductors = [bowtie] });

        Assert.Contains("'strip'", r, StringComparison.Ordinal);
        Assert.Contains("self-intersecting", r, StringComparison.Ordinal);
        Assert.Contains("edge", r, StringComparison.Ordinal);
        Assert.Contains("simple closed polygon", r, StringComparison.Ordinal);
    }

    [Fact]
    public void APortNamingAnAbsentConductor_IsRefusedAndListsTheOnesThatExist()
    {
        var p = Good();
        string r = Refusal(p with { Ports = [new EmPort(1, "sig", null, 50), p.Ports[1]] });
        Assert.Contains("Port 1", r, StringComparison.Ordinal);
        Assert.Contains("'sig'", r, StringComparison.Ordinal);
        Assert.Contains("Known conductors: 'strip'", r, StringComparison.Ordinal);
    }

    [Fact]
    public void APortNamingAnAbsentReferenceConductor_IsRefusedTheSameWay()
    {
        var p = Good();
        string r = Refusal(p with { Ports = [new EmPort(1, "strip", "gnd", 50), p.Ports[1]] });
        Assert.Contains("Port 1", r, StringComparison.Ordinal);
        Assert.Contains("'gnd'", r, StringComparison.Ordinal);
        Assert.Contains("Known conductors: 'strip'", r, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ReferenceConductor == null</c> means "the ground plane". With no ground plane there is no
    /// return path, and the refusal has to say which of the two fixes applies.
    /// </summary>
    [Fact]
    public void APortWithNoResolvableReference_IsRefusedWithBothWaysToFixIt()
    {
        var p = Good();
        string r = Refusal(p with { Ground = null });
        Assert.Contains("no reference conductor", r, StringComparison.Ordinal);
        Assert.Contains("no ground plane", r, StringComparison.Ordinal);
        Assert.Contains("return path", r, StringComparison.Ordinal);
    }

    // ── R-cpl-5: these three refusals are NARROWED by L7b, not deleted. Kernel A refused every
    // multiconductor cross-section by pointing at L7b; L7b ships the symmetric coupled pair, so the
    // same three checks now refuse only what is STILL unsupported and point at L7b-b.

    /// <summary>A port count that does not match the conductor count names the offending conductor,
    /// which is more use than reporting an arithmetic mismatch.</summary>
    [Fact]
    public void MoreThanTwoPortsOnOneConductor_IsRefusedByNamingThatConductor()
    {
        var p = Good();
        var q = p with { Ports = [.. p.Ports, new EmPort(3, "strip", null, 50)] };
        string r = Refusal(q);
        Assert.Contains("'strip' has 3 ports", r, StringComparison.Ordinal);
        Assert.Contains("near end", r, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kernel A's "ports 1 and 2 are on different conductors" generalises to "a port PAIR belongs to
    /// ONE conductor" — which is what that refusal was really protecting all along.
    /// </summary>
    [Fact]
    public void OnePortOnEachOfTwoConductors_IsRefusedBecauseAPortPairBelongsToOneConductor()
    {
        var p = Good();
        var second = new EmConductor("strip2",
            EmProblemBuilders.Rect(2e-3, 1.6e-3, 4e-3, 1.635e-3), EmProblemBuilders.CopperSigma);
        var q = p with
        {
            Conductors = [p.Conductors[0], second],
            Ports = [new EmPort(1, "strip", null, 50), new EmPort(2, "strip2", null, 50)],
        };
        string r = Refusal(q);
        Assert.Contains("has 1 port", r, StringComparison.Ordinal);
        Assert.Contains("its near end and its far end", r, StringComparison.Ordinal);
    }

    /// <summary>
    /// A coupled pair with only ONE conductor ported leaves the other with nothing — refused by
    /// naming the conductor that has no ends, rather than by a port-count arithmetic message.
    /// </summary>
    [Fact]
    public void ACoupledPairWithOnlyOneConductorPorted_IsRefusedByNamingTheUnportedOne()
    {
        var p = Good();
        var second = new EmConductor("strip2",
            EmProblemBuilders.Rect(2e-3, 1.6e-3, 4e-3, 1.635e-3), EmProblemBuilders.CopperSigma);
        string r = Refusal(p with { Conductors = [p.Conductors[0], second] });
        Assert.Contains("'strip2' has 0 ports", r, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>UPDATED by L7b-b — not loosened.</b> This asserted that three signal conductors are
    /// refused by pointing at L7b-b, whose general modal decomposition is what that refusal was
    /// waiting for. N &gt; 2 now SOLVES; the capability boundary moved out to the conductor ceiling
    /// (R-gen-9), which has its own refusal test in <c>GeneralModalTests</c>.
    /// </summary>
    [Fact]
    public void ThreeSignalConductors_AreNowAccepted()
    {
        var p = Good();
        EmConductor Strip(string n, double x0) => new(n,
            EmProblemBuilders.Rect(x0, 1.6e-3, x0 + 1e-3, 1.635e-3), EmProblemBuilders.CopperSigma);

        var q = p with
        {
            Conductors = [p.Conductors[0], Strip("s2", 2e-3), Strip("s3", 4e-3)],
            Ports =
            [
                new EmPort(1, "strip", null, 50), new EmPort(2, "strip", null, 50),
                new EmPort(3, "s2", null, 50),    new EmPort(4, "s2", null, 50),
                new EmPort(5, "s3", null, 50),    new EmPort(6, "s3", null, 50),
            ],
        };
        var verdict = new QuasiStaticKernel().CanSolve(q);
        Assert.True(verdict.Ok, verdict.Reason);
    }

    [Fact]
    public void AZeroLengthLine_IsRefused()
    {
        string r = Refusal(Good() with { LengthMeters = 0 });
        Assert.Contains("propagation length", r, StringComparison.Ordinal);
        Assert.Contains("per-unit-length", r, StringComparison.Ordinal);
    }

    [Fact]
    public void NoConductorsOrNoPortsOrNoRegions_AreEachRefusedSpecifically()
    {
        var p = Good();
        Assert.Contains("no conductors", Refusal(p with { Conductors = [] }), StringComparison.Ordinal);
        Assert.Contains("no ports", Refusal(p with { Ports = [] }), StringComparison.Ordinal);
        Assert.Contains("no dielectric regions", Refusal(p with { Regions = [] }), StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveConductivity_IsRefusedAndNamesThePerfectConductorEscape()
    {
        var p = Good();
        string r = Refusal(p with { Conductors = [p.Conductors[0] with { SigmaSm = 0 }] });
        Assert.Contains("'strip'", r, StringComparison.Ordinal);
        Assert.Contains("PositiveInfinity", r, StringComparison.Ordinal);
    }

    [Fact]
    public void SolveRefusesTheSameWayCanSolveDoes()
    {
        var bad = Good() with { Ground = null };
        var ex = Assert.Throws<InvalidOperationException>(
            () => Kernel.Solve(bad, EmMeshSettings.Default, [1e9], CancellationToken.None));
        Assert.Contains(Kernel.CanSolve(bad).Reason!, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SolveHonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => Kernel.Solve(Good(), EmMeshSettings.Default, [1e9], cts.Token));
    }

    // ── the optional dispersion correction (§7) ───────────────────────────────────────────────

    /// <summary>
    /// Off by default, and it never runs before the static result has been validated on its own.
    /// </summary>
    [Fact]
    public void KirschningJansenDispersion_IsOffByDefaultAndOnlyMovesTheAnswerWhenAskedFor()
    {
        var p = EmProblemBuilders.Fr4Microstrip(2.9e-3);
        double[] freqs = [1e8, 1e10, 2e10];

        var plain = new QuasiStaticKernel().SolveDetailed(p, EmMeshSettings.Default, freqs);
        var disp  = new QuasiStaticKernel(dispersionCorrection: true)
                        .SolveDetailed(p, EmMeshSettings.Default, freqs);

        var e0 = plain.Data["tline.Eeff"].RealValues;
        var e1 = disp.Data["tline.Eeff"].RealValues;

        // Static: frequency-independent by construction.
        Assert.Equal(e0[0], e0[^1], 0.0);

        // Dispersive: rises with frequency, and agrees with the static value at the low end.
        Assert.True(e1[^1] > e1[0], $"εeff(20 GHz) = {e1[^1]:F4} is not above εeff(0.1 GHz) = {e1[0]:F4}");
        Assert.Equal(e0[0], e1[0], e0[0] * 0.01);
        Assert.True(e1[^1] > e0[^1] * 1.02, "the correction should be visible by 20 GHz on 1.6 mm FR-4");
    }

    [Fact]
    public void DispersionIsSkippedForAGeometryItWasNeverDerivedFor()
    {
        // Two conductors: not a single microstrip, so there is nothing K-J applies to.
        var p = Good();
        var second = new EmConductor("strip2",
            EmProblemBuilders.Rect(2e-3, 1.6e-3, 4e-3, 1.635e-3), EmProblemBuilders.CopperSigma);
        Assert.Null(QuasiStaticKernel.TryMicrostripDispersion(p with { Conductors = [p.Conductors[0], second] }));

        // No ground plane: likewise.
        Assert.Null(QuasiStaticKernel.TryMicrostripDispersion(p with { Ground = null }));

        var ok = QuasiStaticKernel.TryMicrostripDispersion(p);
        Assert.NotNull(ok);
        Assert.Equal(2.9e-3 / 1.6e-3, ok.WOverH, 1e-9);
        Assert.Equal(4.4, ok.EpsR, 1e-12);
        Assert.Equal(1.6e-3, ok.HMeters, 1e-12);
    }

    [Fact]
    public void PortZ0IsHonouredPerPortAndMayBeComplex()
    {
        var p = Good() with
        {
            Ports = [new EmPort(1, "strip", null, new Complex(75, 0)),
                     new EmPort(2, "strip", null, new Complex(50, -10))],
        };
        var ds = Kernel.Solve(p, EmMeshSettings.Default, [5e9], CancellationToken.None);
        var z0 = ds["Z0"].ComplexValues;
        Assert.Equal(new Complex(75, 0), z0[0]);
        Assert.Equal(new Complex(50, -10), z0[1]);
    }
}
