using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>L7b Tiers C1–C4</b> — the coupled symmetric pair (docs/sonnet-briefs/brief-L7b-coupled-lines-and-cosim.md §6).
///
/// <para>Tier C1's <i>exact</i> oracle lives in <see cref="ClosedFormCapacitanceTests"/> beside the
/// single-line one it extends; what lives here is everything that needs the mesher, the extractor or
/// the s-parameter path.</para>
/// </summary>
public class CoupledLineTests(ITestOutputHelper output)
{
    // A realistic edge-coupled pair on the FR-4 starter stackup: W/h ≈ 0.9, S/h ≈ 0.19 — tight
    // enough that the coupling is unambiguous and the off-diagonals actually matter.
    private const double W = 1.4e-3, S = 0.3e-3;

    private static RlgcModel Extract(EmProblem p, EmMeshSettings? settings = null)
        => RlgcExtractor.Extract(p, BoundaryMesher.Mesh(p, settings ?? EmMeshSettings.Default));

    private static CoupledModes Modes(EmProblem p, double refine = 2.0)
        => ModalDecomposition.Decompose(Extract(p, EmMeshSettings.Default.Refined(refine)));

    private static DataSet Solve(EmProblem p, double[] freqs)
        => new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2), freqs, CancellationToken.None);

    /// <summary>The 4×4 S at frequency index <paramref name="fi"/>.</summary>
    private static Mat<Complex> S4(DataSet ds, int fi)
    {
        var v = ds["S"].ComplexValues;
        var m = new Mat<Complex>(4, 4);
        for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++)
            m[i, j] = v[fi * 16 + i * 4 + j];
        return m;
    }

    /// <summary>An ideal pair: perfect metal, zero tanδ — so losslessness is exact, not approximate.</summary>
    private static EmProblem Ideal(double s = S, double lengthM = 0.02)
        => EmProblemBuilders.CoupledMicrostrip(
            W, s, 1.6e-3, 35e-6, 4.4, tanD: 0,
            sigmaSm: double.PositiveInfinity, lengthMeters: lengthM,
            groundSigmaSm: double.PositiveInfinity);

    private static readonly double[] Sweep = [1e8, 1e9, 5e9, 1e10];

    // ── Tier C1 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Tier C1.</b> R-cpl-7's residual is a DISCRETISATION-error indicator, so refining the
    /// discretisation must shrink it. If it does not, something else is wrong and this is the test
    /// that says so — which is why the brief asks for the behaviour rather than for a threshold.
    /// </summary>
    [Fact]
    public void TC1_ResidualFallsUnderMeshRefinement()
    {
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, S);

        double coarse = Extract(p, EmMeshSettings.Default).AsymmetryResidual;
        double fine   = Extract(p, EmMeshSettings.Default.Refined(2)).AsymmetryResidual;

        output.WriteLine($"R-cpl-7 residual: default = {coarse:P3}, Refined(2) = {fine:P3}");

        Assert.True(coarse > 0,
            "an exactly-symmetric matrix would make this test vacuous — the residual exists because " +
            "the mesher discretises the two conductors differently");
        Assert.True(fine < coarse,
            $"the residual must fall under refinement: default gave {coarse:P3}, Refined(2) gave {fine:P3}");
    }

    /// <summary>
    /// <b>Tier C1.</b> The residual is reported as a NAMED NUMBER in the RLGC notes, not left as an
    /// internal step — a user tightening the mesh has to be able to watch it fall.
    /// </summary>
    [Fact]
    public void TC1_ResidualIsReportedInTheNotes()
    {
        var rlgc = Extract(EmProblemBuilders.Fr4CoupledMicrostrip(W, S));
        Assert.Contains(rlgc.Notes, n => n.Contains("asymmetry residual", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Tier C1.</b> A single line reports no residual at all — there are no off-diagonals to be
    /// asymmetric about, and a spurious note there would be noise on every ordinary microstrip run.
    /// </summary>
    [Fact]
    public void TC1_SingleLineReportsNoResidual()
    {
        var rlgc = Extract(EmProblemBuilders.Fr4Microstrip(2.9e-3));
        Assert.Equal(0.0, rlgc.AsymmetryResidual);
        Assert.DoesNotContain(rlgc.Notes, n => n.Contains("asymmetry residual", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Tier C1 / R-cpl-2.</b> Each conductor is receded ALONE, so a pair yields one ∂[L]/∂n per
    /// conductor plus the ground — three surfaces, not the two a single line has. Receding them
    /// together would sum two surfaces into one derivative that cannot be taken apart again.
    /// </summary>
    [Fact]
    public void TC1_EachConductorGetsItsOwnRecession()
    {
        var rlgc = Extract(EmProblemBuilders.Fr4CoupledMicrostrip(W, S));

        Assert.Equal(3, rlgc.LossSurfaces.Count);
        Assert.Equal("conductor:a", rlgc.LossSurfaces[0].Name);
        Assert.Equal("conductor:b", rlgc.LossSurfaces[1].Name);
        Assert.Equal("ground",      rlgc.LossSurfaces[2].Name);

        // Receding conductor k must raise ITS OWN inductance…
        Assert.True(rlgc.LossSurfaces[0].DLdn[0, 0] > 0);
        Assert.True(rlgc.LossSurfaces[1].DLdn[1, 1] > 0);

        // …and by symmetry the two conductors' own-derivatives agree.
        Assert.Equal(rlgc.LossSurfaces[0].DLdn[0, 0], rlgc.LossSurfaces[1].DLdn[1, 1],
                     rlgc.LossSurfaces[0].DLdn[0, 0] * 0.05);

        // R-mom-11 is unchanged: 2 capacitance solves + 2 conductor recessions + 1 ground = 5.
        Assert.Equal(5, rlgc.MatrixFillCount);
    }

    /// <summary>
    /// <b>Tier C1 / R-cpl-3.</b> R_dc is diagonal — each conductor has its own DC series resistance,
    /// and adding them is only right when there is one.
    /// </summary>
    [Fact]
    public void TC1_RdcIsDiagonalAndPerConductor()
    {
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, S);
        var rlgc = Extract(p);

        double want = 1.0 / (EmProblemBuilders.CopperSigma * W * 35e-6);
        Assert.Equal(want, rlgc.RdcPerM[0, 0], want * 1e-9);
        Assert.Equal(want, rlgc.RdcPerM[1, 1], want * 1e-9);
        Assert.Equal(0.0,  rlgc.RdcPerM[0, 1]);
        Assert.Equal(0.0,  rlgc.RdcPerM[1, 0]);
    }

    // ── Tier C2 — the self-consistency oracles, which need no external data ───────────────────

    /// <summary>
    /// <b>Tier C2, and the strongest oracle in the ladder.</b> A pair pushed far apart IS two
    /// independent single lines: C₁₂ → 0, Z_e → Z_o → the isolated line's own Z₀, and both ε_eff
    /// converge to the single-line value.
    ///
    /// <para>Asserted against kernel A's <b>own</b> single-line result for the same width, so the
    /// coupled path is checked against the already-validated path rather than against a number typed
    /// into a test.</para>
    /// </summary>
    [Fact]
    public void TC2_AFarApartPair_ReproducesTwoIndependentSingleLines()
    {
        var single = Extract(EmProblemBuilders.Microstrip(W, 1.6e-3, 35e-6, 4.4),
                             EmMeshSettings.Default.Refined(2));
        double z0Single = Math.Sqrt(single.LPerM / single.CPerM);

        var m = Modes(EmProblemBuilders.Fr4CoupledMicrostrip(W, 20e-3));

        Assert.Equal(z0Single, m.Even.Z0, z0Single * 0.01);
        Assert.Equal(z0Single, m.Odd.Z0,  z0Single * 0.01);
        Assert.Equal(single.Eeff, m.Even.Eeff, single.Eeff * 0.01);
        Assert.Equal(single.Eeff, m.Odd.Eeff,  single.Eeff * 0.01);
    }

    /// <summary>
    /// <b>Tier C2.</b> The same limit through the whole s-parameter path: with the conductors far
    /// apart the 4-port degenerates into two uncoupled 2-ports, so nothing crosses from conductor A
    /// to conductor B. This is what pins the <c>[[Zs, Zm], [Zm, Zs]]</c> block construction — a
    /// transposed or mis-scaled mutual block would leave coupling behind here.
    /// </summary>
    [Fact]
    public void TC2_AFarApartPair_HasNoCrossCouplingInTheSMatrix()
    {
        var ds = Solve(EmProblemBuilders.CoupledMicrostrip(W, 20e-3, 1.6e-3, 35e-6, 4.4), [5e9]);
        var s = S4(ds, 0);

        // Ports 1,2 are conductor A's two ends; 3,4 are conductor B's (D3).
        Assert.True(s[0, 2].Magnitude < 0.02, $"|S31| = {s[0, 2].Magnitude:F4} is not uncoupled");
        Assert.True(s[0, 3].Magnitude < 0.02, $"|S41| = {s[0, 3].Magnitude:F4} is not uncoupled");
        // …while the through path on conductor A is fully present.
        Assert.True(s[0, 1].Magnitude > 0.5, $"|S21| = {s[0, 1].Magnitude:F4} lost the through path");
    }

    /// <summary>
    /// <b>Tier C2 / R-cpl-9 — the sign gate.</b> <c>Z_o &lt; Z_e</c> is true for every real coupled
    /// line, and it is the cheapest possible check that C₁₂'s sign convention was not inverted. A
    /// Maxwell matrix has NEGATIVE off-diagonals; reading it as a "mutual capacitance" matrix
    /// (positive off-diagonals) swaps even and odd, and both answers look physical.
    /// </summary>
    [Theory]
    [InlineData(0.2e-3)]
    [InlineData(0.3e-3)]
    [InlineData(1.0e-3)]
    [InlineData(3.0e-3)]
    public void TC2_ZoddIsBelowZeven_AtEverySpacing(double gap)
    {
        var m = Modes(EmProblemBuilders.Fr4CoupledMicrostrip(W, gap));
        Assert.True(m.SignConventionHolds,
            $"S = {gap * 1e3:F1} mm: Z_o = {m.Odd.Z0:F2} Ω is not below Z_e = {m.Even.Z0:F2} Ω — " +
            "the Maxwell off-diagonal sign convention has been inverted");
    }

    /// <summary>
    /// <b>Tier C2.</b> Tighter coupling must widen the split, monotonically — the modal quantities
    /// have to respond to the gap in the right direction, not merely differ.
    /// </summary>
    [Fact]
    public void TC2_TighterCouplingWidensTheModalSplit()
    {
        double prev = double.MaxValue;
        foreach (double gap in new[] { 0.2e-3, 0.5e-3, 1.5e-3, 5e-3 })
        {
            var m = Modes(EmProblemBuilders.Fr4CoupledMicrostrip(W, gap));
            double split = m.Even.Z0 - m.Odd.Z0;
            Assert.True(split < prev, $"S = {gap * 1e3:F1} mm: split {split:F2} Ω did not shrink from {prev:F2} Ω");
            prev = split;
        }
    }

    /// <summary>
    /// <b>Tier C2 / R-cpl-10 — reciprocity is STRUCTURAL.</b> The 4-port Z is built as
    /// <c>[[Zs, Zm], [Zm, Zs]]</c> out of two symmetric 2×2 mode matrices, so S = Sᵀ falls out of the
    /// construction rather than being hoped for. Checked on a LOSSY pair, where an asymmetry would
    /// have somewhere to hide.
    /// </summary>
    [Fact]
    public void TC2_TheFourPortIsReciprocal()
    {
        var ds = Solve(EmProblemBuilders.Fr4CoupledMicrostrip(W, S, tanD: 0.02), Sweep);
        for (int f = 0; f < Sweep.Length; f++)
        {
            var s = S4(ds, f);
            for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                Assert.Equal(s[i, j].Real,      s[j, i].Real,      1e-12);
                Assert.Equal(s[i, j].Imaginary, s[j, i].Imaginary, 1e-12);
            }
        }
    }

    /// <summary>
    /// <b>Tier C2.</b> Passivity of the 4-port: no incident power vector leaves with more than it
    /// brought. Checked as <c>σ_max(S) ≤ 1</c>, which is exactly <c>I − SᴴS ⪰ 0</c> in the form
    /// <c>RFNetwork.Passivity</c> already computes for every other network in this project.
    /// </summary>
    [Fact]
    public void TC2_TheFourPortIsPassive()
    {
        var ds = Solve(EmProblemBuilders.Fr4CoupledMicrostrip(W, S, tanD: 0.02), Sweep);
        for (int f = 0; f < Sweep.Length; f++)
        {
            double sigmaMax = RfCore.RFNetwork.Passivity(S4(ds, f));
            Assert.True(sigmaMax <= 1.0 + 1e-9,
                $"f = {Sweep[f]:G4} Hz: σ_max(S) = {sigmaMax:F9} exceeds 1 — the 4-port is active");
        }
    }

    /// <summary>
    /// <b>Tier C2.</b> With perfect metal and zero tanδ every column of S is unitary: all the power
    /// injected at one port leaves through the four. This is the property that would break first if
    /// the even/odd superposition dropped or double-counted a factor of two.
    /// </summary>
    [Fact]
    public void TC2_TheFourPortIsLosslessWhenEveryLossIsIdeal()
    {
        var ds = Solve(Ideal(), Sweep);
        for (int f = 0; f < Sweep.Length; f++)
        {
            var s = S4(ds, f);
            for (int j = 0; j < 4; j++)
            {
                double sum = 0;
                for (int i = 0; i < 4; i++) sum += s[i, j].Magnitude * s[i, j].Magnitude;
                Assert.Equal(1.0, sum, 1e-9);
            }
        }

        foreach (string mode in new[] { "Even", "Odd" })
        {
            foreach (double a in ds[$"tline.AttenDbPerM{mode}"].RealValues) Assert.Equal(0.0, a, 1e-12);
            foreach (double r in ds[$"tline.Rpul{mode}"].RealValues)         Assert.Equal(0.0, r, 1e-15);
            foreach (double g in ds[$"tline.Gpul{mode}"].RealValues)         Assert.Equal(0.0, g, 1e-15);
        }
    }

    /// <summary>
    /// <b>Tier C2 / D3 — the port map, pinned by a test whose WRONG pairing fails.</b> Ports 2k−1 and
    /// 2k are the two ends of conductor k, so on a weakly coupled pair S21 (the THROUGH path along
    /// conductor A) must dominate S31 (the COUPLED path onto conductor B).
    ///
    /// <para>Under the transposition this exists to catch — numbering the near ends 1,2 and the far
    /// ends 3,4 — S21 would BE the coupled term and this inequality would invert. A transposed map
    /// produces a coupler whose through and coupled ports are swapped: smooth, plausible, wrong, and
    /// invisible in a magnitude plot of a symmetric structure.</para>
    /// </summary>
    [Fact]
    public void TC2_D3PortMap_ThroughDominatesCoupled_AndTheTransposedMapWouldFail()
    {
        var ds = Solve(EmProblemBuilders.Fr4CoupledMicrostrip(W, 2.0e-3), [5e9]);
        var s = S4(ds, 0);

        double through = s[0, 1].Magnitude;   // port 1 → port 2: conductor A, near → far
        double coupled = s[0, 2].Magnitude;   // port 1 → port 3: onto conductor B

        Assert.True(through > 0.5, $"|S21| = {through:F4} is not a through path");
        Assert.True(coupled < 0.3, $"|S31| = {coupled:F4} is not a weakly coupled path");
        Assert.True(through > 3 * coupled,
            $"through |S21| = {through:F4} does not dominate coupled |S31| = {coupled:F4} — " +
            "the D3 port map has been transposed");
    }

    // ── Tier C3 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Tier C3 — a physical limiting case against an INDEPENDENTLY COMPUTED geometry.</b>
    ///
    /// <para>As the gap closes, two strips of width W separated by S become one strip of width
    /// 2W + S. In the EVEN mode both conductors sit at the same potential, so the pair <i>is</i> that
    /// single wide strip and the total even-mode capacitance to ground, 2·C_even, must approach the
    /// wide strip's own C — computed by the same solver on a different geometry it knows nothing
    /// about. Measured: −1.10% at S = 400 µm falling monotonically to <b>−0.075% at S = 20 µm</b>.</para>
    ///
    /// <para>This directly exercises the C_even combination, and therefore R-cpl-9's sign convention:
    /// with C₁₂'s sign inverted, C_even and C_odd swap and this limit misses by tens of percent.</para>
    ///
    /// <para><b>This is a substitute for the brief's published even/odd fit, and the substitution is
    /// deliberate — see the phase note.</b> No fit whose inputs could be verified was obtainable, and
    /// this codebase's own history (Hammerstad-Jensen's thickness model, the Garg-Bahl gap at L5a) is
    /// that an unverifiable "oracle" is worse than none.</para>
    /// </summary>
    [Fact]
    public void TC3_MergedStripLimit_TheEvenModeReproducesASingleWideStrip()
    {
        const double w = 1.0e-3, h = 1.6e-3, t = 35e-6, er = 4.4;
        double prevErr = double.MaxValue;

        foreach (double gap in new[] { 0.4e-3, 0.2e-3, 0.1e-3, 0.05e-3, 0.02e-3 })
        {
            var modes = Modes(EmProblemBuilders.CoupledMicrostrip(w, gap, h, t, er));
            double pairTotal = 2.0 * modes.Even.CPerM;

            double wide = Extract(EmProblemBuilders.Microstrip(2 * w + gap, h, t, er),
                                  EmMeshSettings.Default.Refined(2)).CPerM;

            double err = Math.Abs(pairTotal - wide) / wide;
            output.WriteLine($"S = {gap * 1e6,4:F0} µm: 2·C_even = {pairTotal:E4}, " +
                             $"C(single {2 * w + gap:E2} m strip) = {wide:E4}, error {err:P3}");

            Assert.True(err < prevErr,
                $"S = {gap * 1e6:F0} µm: error {err:P3} did not improve on {prevErr:P3} — the merged " +
                "limit must converge as the gap closes");
            prevErr = err;
        }

        Assert.True(prevErr < 0.005,
            $"the tightest gap still misses the merged-strip limit by {prevErr:P3}");
    }

    // ── Tier C4 — refusals stay specific ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>Tier C4, UPDATED by L7b-b — not loosened.</b> This test asserted that two lines of
    /// different widths are refused by name pointing at L7b-b. L7b-b is what that refusal was
    /// pointing at, and it accepts them: the general modal decomposition handles an asymmetric pair
    /// correctly rather than approximately. What survives is the useful half — the geometric check
    /// still ANSWERS the question, naming both numbers, because that is what tells a user the
    /// even/odd vocabulary does not apply to their cross-section; it simply no longer refuses.
    /// </summary>
    [Fact]
    public void TC4_AnAsymmetricPair_IsNowAccepted_AndTheGeometricCheckStillNamesBothWidths()
    {
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, S, w2: 2 * W);

        Assert.True(new QuasiStaticKernel().CanSolve(p).Ok,
            "an asymmetric pair is exactly what the general modal decomposition exists for");

        string? why = ModalDecomposition.CheckGeometricSymmetry(p);
        Assert.NotNull(why);
        Assert.Contains("not mirror-symmetric", why, StringComparison.Ordinal);
        Assert.Contains("width", why, StringComparison.Ordinal);
        // Both numbers, not just an assertion that they differ.
        Assert.Contains("0.0014", why, StringComparison.Ordinal);
        Assert.Contains("0.0028", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Tier C4, UPDATED by L7b-b — not loosened.</b> A pair on two different metal levels is
    /// likewise no longer refused; the geometric check still reports the reason that actually
    /// applies to it rather than a width message.
    /// </summary>
    [Fact]
    public void TC4_APairOnTwoDifferentMetalLevels_IsNowAccepted_AndReportedByTheRightReason()
    {
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, S);
        var lifted = p.Conductors[1];
        var raised = new List<EmPoint>();
        foreach (var v in lifted.Outline) raised.Add(v with { Y = v.Y + 200e-6 });

        var stacked = p with { Conductors = [p.Conductors[0], lifted with { Outline = raised }] };

        Assert.True(new QuasiStaticKernel().CanSolve(stacked).Ok);

        string? why = ModalDecomposition.CheckGeometricSymmetry(stacked);
        Assert.NotNull(why);
        Assert.Contains("not mirror-symmetric", why, StringComparison.Ordinal);
        Assert.Contains("lower surface", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Tier C4.</b> A geometrically SYMMETRIC pair whose mesh cannot yet resolve the two
    /// conductors alike is <b>warned about, never refused</b> — the fix is a finer mesh, not L7b-b,
    /// and telling the user otherwise would send them somewhere that cannot help.
    /// </summary>
    [Fact]
    public void TC4_AnUnderResolvedSymmetricPair_IsWarnedAboutRatherThanRefused()
    {
        // t/W ≈ 1/1400 — an extreme aspect ratio the default mesh genuinely under-resolves.
        var p = EmProblemBuilders.CoupledMicrostrip(1.4e-3, 0.3e-3, 1.6e-3, 1e-6, 4.4);

        Assert.True(new QuasiStaticKernel().CanSolve(p).Ok,
            "a pair that is symmetric BY CONSTRUCTION must never be refused for being asymmetric");

        var rlgc = Extract(p);
        Assert.True(ModalDecomposition.DiagonalAsymmetry(rlgc)
                    > ModalDecomposition.DiagonalAsymmetryWarnThreshold,
            "this fixture is chosen to be under-resolved; if it no longer is, the warning path is untested");
        Assert.Contains(rlgc.Notes, n => n.Contains("Refine the mesh", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Tier C4.</b> A symmetric pair with the right 4 ports SOLVES — the narrowed refusals let
    /// through exactly what L7b ships, which is the other half of "narrow, do not delete".
    /// </summary>
    [Fact]
    public void TC4_ASymmetricPairWithFourPorts_IsAccepted()
    {
        var verdict = new QuasiStaticKernel().CanSolve(EmProblemBuilders.Fr4CoupledMicrostrip(W, S));
        Assert.True(verdict.Ok, verdict.Reason);
    }
}
