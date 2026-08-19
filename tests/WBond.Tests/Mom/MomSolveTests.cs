using System.Numerics;
using CircuitRF.WBond.Mom;
using NumFlat;
using RfCore;

namespace CircuitRF.WBond.Tests.Mom;

/// <summary>
/// The gates of brief-wbond-mom-w2 §6: the identity against the already-validated analytic path, the
/// end-to-end low-frequency series gate, structure, passivity, losslessness and network convergence.
///
/// <para><b>§6.2 is written first and is the one to debug first.</b> It compares the segment mesh's own
/// series arm against <see cref="ImpedanceReduction.ArrayImpedance"/> — the wire-basis code the
/// repository already trusts — in milliseconds, and it validates the segment <b>L</b>, the segment
/// <b>D(ω)</b>, the wire grouping and the array reduction in one assertion.</para>
///
/// <h3>§6.6's correlation study is NOT here, and it cannot be</h3>
/// <para>It compares against <c>WBondTouchstoneExport.TerminalAdmittances</c>, which lives in
/// <c>src/Ui</c>; <c>src/WBond</c> is a leaf project and this test assembly does not reference the UI.
/// The study is in <c>tests/Ui.Tests/WBondMomCompareTests.cs</c>, driven through the same view model
/// the Compare dialog uses, so the number the test asserts and the number the dialog shows are one
/// computation rather than two.</para>
/// </summary>
public sealed class MomSolveTests
{
    /// <summary>Two arrays of two ball bonds — skew filaments throughout, so no closed form saves us.</summary>
    private static WBondDesign FourWireTwoArray() =>
        TestDesigns.PowerAmplifier(wireCount: 4, arrayCount: 2, pointsPerWire: 7);

    private static WireMomSolver Solver(WBondDesign design, int segments = 24) =>
        WireMomSolver.Create(design, WireMomSettings.Default with { TargetSegmentsPerWire = segments });

    private static double WorstRelative(Complex[] a, Complex[] b)
    {
        double worst = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double scale = Math.Max(a[i].Magnitude, b[i].Magnitude);
            if (scale == 0.0) continue;
            worst = Math.Max(worst, (a[i] - b[i]).Magnitude / scale);
        }
        return worst;
    }

    /// <summary>
    /// Array <i>k</i>'s series impedance, read off the full <c>Y_port</c>.
    ///
    /// <para>Valid wherever the shunt is negligible against the series arm — at 10 MHz a ~35 fF shunt is
    /// ~455 kΩ against a ~0.1 Ω arm, six orders of margin.</para>
    /// </summary>
    private static Complex SeriesFromY(Complex[] y, int t, int array) => -1.0 / y[2 * array * t + 2 * array + 1];

    private static Mat<Complex> ToMat(Complex[] flat, int n)
    {
        var mat = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                mat[i, j] = flat[i * n + j];
        return mat;
    }

    // ---------------------------------------------------------------- 6.2

    /// <summary>
    /// <b>The identity gate.</b> With the shunt path removed by construction, a 24-segment mesh and the
    /// wire basis describe the same circuit: KCL forces one current per wire, partial inductance is
    /// additive under subdivision, and <c>D</c> scales with length. So
    /// <see cref="WireMomSolver.SeriesArmImpedance"/> must reproduce
    /// <see cref="ImpedanceReduction.ArrayImpedance"/> to solver precision, not to a tolerance.
    ///
    /// <para>Measured 3.23e-11 at all three frequencies — set by WM-1's own inductance identity
    /// (1.5e-10 on a curved wire, limited by <see cref="Grover.Skew"/>'s cancellation), not by anything
    /// added here.</para>
    /// </summary>
    [Theory]
    [InlineData(10e6)]
    [InlineData(1e9)]
    [InlineData(20e9)]
    public void SeriesArm_OnASubdividedMesh_IsTheAnalyticArrayImpedance(double frequencyHz)
    {
        var design = FourWireTwoArray();

        var mom = Solver(design).SeriesArmImpedance(frequencyHz);
        var analytic = ImpedanceReduction.Create(design).ArrayImpedance(frequencyHz);

        double worst = WorstRelative(mom, analytic);
        Assert.True(worst < 1e-10,
            $"Series arm vs ImpedanceReduction at {frequencyHz:0.###E+0} Hz: worst relative {worst:E3}.");
    }

    // ---------------------------------------------------------------- 6.1

    /// <summary>
    /// <c>Z_port</c> is complex symmetric <b>before</b> anything forces it to be.
    ///
    /// <para>Asserted on the raw matrix deliberately: <see cref="WireMomSolver.PortImpedance"/>
    /// symmetrises its output so reciprocity is structural in what callers get, and a gate that ran
    /// after that step would be testing the symmetriser rather than the solve.</para>
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(10e9)]
    [InlineData(40e9)]
    public void PortImpedance_IsSymmetricBeforeItIsSymmetrised(double frequencyHz)
    {
        var solver = Solver(FourWireTwoArray());
        int t = solver.TerminalCount;

        var raw = solver.PortImpedance(frequencyHz, symmetrise: false);

        double worst = 0.0;
        for (int i = 0; i < t; i++)
            for (int j = i + 1; j < t; j++)
            {
                double scale = Math.Max(raw[i * t + j].Magnitude, raw[j * t + i].Magnitude);
                if (scale == 0.0) continue;
                worst = Math.Max(worst, (raw[i * t + j] - raw[j * t + i]).Magnitude / scale);
            }

        Assert.True(worst < 1e-12, $"Raw Z_port asymmetry at {frequencyHz:0.###E+0} Hz: {worst:E3}.");
    }

    [Theory]
    [InlineData(1e9)]
    [InlineData(10e9)]
    [InlineData(40e9)]
    public void PortAdmittance_IsTheInverseOfPortImpedance(double frequencyHz)
    {
        var solver = Solver(FourWireTwoArray());
        int t = solver.TerminalCount;

        var z = solver.PortImpedance(frequencyHz);
        var y = solver.PortAdmittance(frequencyHz);

        double worst = 0.0;
        for (int i = 0; i < t; i++)
            for (int j = 0; j < t; j++)
            {
                Complex acc = Complex.Zero;
                for (int k = 0; k < t; k++) acc += z[i * t + k] * y[k * t + j];
                worst = Math.Max(worst, (acc - (i == j ? Complex.One : Complex.Zero)).Magnitude);
            }

        Assert.True(worst < 1e-10, $"Z·Y − I at {frequencyHz:0.###E+0} Hz: {worst:E3}.");
    }

    /// <summary>T = 2M, and the terminal names are the exported file's own port order.</summary>
    [Fact]
    public void TerminalCountAndNames_AreTwoPerArray_InTheDesignsOwnOrder()
    {
        var design = FourWireTwoArray();
        var solver = Solver(design);

        Assert.Equal(2 * design.Arrays.Count, solver.TerminalCount);
        Assert.Equal(WireMomMesh.TerminalNamesFor(design), solver.TerminalNames);
        Assert.Equal(["G1.i", "G1.o", "G2.i", "G2.o"], solver.TerminalNames);
    }

    // ---------------------------------------------------------------- 6.3

    /// <summary>
    /// <b>The end-to-end gate.</b> At 10 MHz each array's series impedance, read off the full
    /// <c>Y_port</c>, must be the analytic one — and getting there goes through the mesh, <b>P</b>,
    /// <b>G</b>, <b>K̃</b>, <b>W</b>, <b>H</b>, <b>M̃</b>, the factorisation and the port reduction, so
    /// nothing in the chain can be wrong and leave this green.
    ///
    /// <h3>The brief's named oracle is the wrong one, and by 5.6 %</h3>
    /// <para>brief-wbond-mom-w2 §6.3 asks for the imaginary part over ω to match
    /// <c>ArrayReduction.PicoHenries(k,k)</c>. <b>That is the EXTERNAL partial inductance only.</b>
    /// <see cref="ArrayReduction"/> consumes <b>L</b> and <b>A</b> and nothing else — the internal
    /// inductance lives in <c>D(ω)</c>, which never reaches it. At DC that is μ₀/8π per unit length:
    /// 127 pH on a 100 mil wire, 63.5 pH for two of them in parallel, against a 1,065 pH external
    /// value. So the gate as written would fail at 0.1 % by a factor of 56, on a correct solver.
    /// The right oracle — and the one §6.3 already names for the resistance — is
    /// <see cref="ImpedanceReduction.ArrayImpedance"/>, which is what the analytic stamp itself uses.
    /// The external-only value is asserted separately below, so the 5.6 % is pinned as a measurement
    /// rather than left as a story.</para>
    ///
    /// <h3>The tolerance is 1e-5 relative, not the brief's 1e-3</h3>
    /// <para>Measured at 8.6e-8 relative on two straight wires and 2.6e-8 on four ball bonds — four
    /// orders inside the brief's 0.1 %, so the gate is tightened by two of them and still carries 100×
    /// of margin.</para>
    /// </summary>
    [Fact]
    public void At10MHz_TheSeriesArmFromYPort_IsTheAnalyticOne()
    {
        var design = FourWireTwoArray();
        const double f = 10e6;
        double omega = 2.0 * Math.PI * f;

        var solver = Solver(design);
        var analytic = ImpedanceReduction.Create(design).ArrayImpedance(f);

        int t = solver.TerminalCount, m = solver.ArrayCount;
        var y = solver.PortAdmittance(f);

        for (int k = 0; k < m; k++)
        {
            var series = SeriesFromY(y, t, k);
            var expected = analytic[k * m + k];

            double dL = Math.Abs(series.Imaginary - expected.Imaginary) / Math.Abs(expected.Imaginary);
            double dR = Math.Abs(series.Real - expected.Real) / Math.Abs(expected.Real);

            Assert.True(dL < 1e-5, $"Array {k}: L from Y_port {series.Imaginary / omega * 1e12:F4} pH vs " +
                                   $"analytic {expected.Imaginary / omega * 1e12:F4} pH — {dL:E3} relative.");
            Assert.True(dR < 1e-5, $"Array {k}: R from Y_port {series.Real:E6} vs analytic " +
                                   $"{expected.Real:E6} — {dR:E3} relative.");
        }
    }

    /// <summary>
    /// The measurement behind the correction above: the external-only reduction really is several
    /// percent low, so nobody re-points the gate at it.
    /// </summary>
    [Fact]
    public void TheExternalOnlyArrayReduction_IsSeveralPercentBelowTheSeriesArm_BecauseItHasNoInternalInductance()
    {
        var design = TestDesigns.ParallelArray(n: 2, pitchMil: 10, lengthMil: 100, heightMil: 10, arrays: 1);
        const double f = 10e6;
        double omega = 2.0 * Math.PI * f;

        var reduction = ImpedanceReduction.Create(design);
        double withInternal = reduction.ArrayImpedance(f)[0].Imaginary / omega;
        double externalOnly = reduction.InductanceOnlyReduction().PicoHenries(0, 0);

        double gap = (withInternal * 1e12 - externalOnly) / externalOnly;
        Assert.True(gap > 0.05 && gap < 0.07,
            $"Internal inductance is {100 * gap:F2} % of the external value here — expected ~5.96 %.");
    }

    // ---------------------------------------------------------------- 6.4

    /// <summary>
    /// Passivity: the Hermitian part of <c>Z_port</c> is positive semidefinite at every frequency.
    /// T = 4, so the eigenvalues are free.
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(10e9)]
    [InlineData(40e9)]
    public void ZPort_IsPassive(double frequencyHz)
    {
        var solver = Solver(FourWireTwoArray());
        int t = solver.TerminalCount;
        var z = solver.PortImpedance(frequencyHz);

        var hermitian = new Mat<double>(t, t);
        double scale = 0.0;
        for (int i = 0; i < t; i++)
            for (int j = 0; j < t; j++)
            {
                double v = 0.5 * (z[i * t + j].Real + z[j * t + i].Real);
                hermitian[i, j] = v;
                scale = Math.Max(scale, Math.Abs(v));
            }

        var eigen = hermitian.Evd();
        double smallest = double.PositiveInfinity;
        for (int i = 0; i < t; i++) smallest = Math.Min(smallest, eigen.D[i]);

        Assert.True(smallest > -1e-12 * scale,
            $"Smallest eigenvalue of Re(Z_port) at {frequencyHz:0.###E+0} Hz is {smallest:E3} " +
            $"against a scale of {scale:E3}.");
    }

    /// <summary>
    /// Losslessness: with the metal made perfect the wires have no resistance, and nothing else in this
    /// kernel dissipates — no dielectric, no radiation. So S must be unitary.
    ///
    /// <h3>σ = 1e12 S/m is NOT lossless, and the brief's 1e-9 is unreachable there</h3>
    /// <para>brief-wbond-mom-w2 §6.4 says to set σ = 1e12 "so R → 0" and asserts unitarity to 1e-9.
    /// <b>Measured, 1e12 leaves |S†S − I| at 2.2e-5 (1 GHz) to 3.8e-5 (10 GHz)</b> — four orders above
    /// the stated gate, on a solver that is correct. The residual is the wires' own skin-effect
    /// resistance and nothing else, which this test proves rather than assumes: swept over
    /// σ ∈ {1e12, 1e14, 1e16, 1e20} the defect falls as <b>1/√σ to three digits</b>, which is exactly
    /// the high-frequency resistance law and is not a law any numerical artefact obeys.</para>
    ///
    /// <para>So the gate is taken at σ = 1e20, where the defect is 2.2e-9 to 3.8e-9, and the scaling is
    /// asserted alongside it. Asserting both is what makes this a losslessness gate rather than a
    /// tolerance nobody can interpret.</para>
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(10e9)]
    [InlineData(40e9)]
    public void WithNoLoss_TheSMatrixIsUnitary_AndWhatRemainsIsSkinResistance(double frequencyHz)
    {
        double Defect(double sigma)
        {
            var design = FourWireTwoArray();
            var perfect = new WireMaterial("Perfect", sigma, 0.0, 19_300);
            design.Materials.Insert(0, perfect);
            foreach (var wire in design.AllWires()) wire.Material = perfect.Name;

            var solver = Solver(design);
            int t = solver.TerminalCount;
            var s = RFNetwork.ZToS(ToMat(solver.PortImpedance(frequencyHz), t), new Complex(50.0, 0.0));

            double worst = 0.0;
            for (int i = 0; i < t; i++)
                for (int j = 0; j < t; j++)
                {
                    Complex acc = Complex.Zero;
                    for (int k = 0; k < t; k++) acc += Complex.Conjugate(s[k, i]) * s[k, j];
                    worst = Math.Max(worst, (acc - (i == j ? Complex.One : Complex.Zero)).Magnitude);
                }
            return worst;
        }

        double perfect = Defect(1e20);
        Assert.True(perfect < 1e-8,
            $"|S†S − I| at {frequencyHz:0.###E+0} Hz with sigma = 1e20 is {perfect:E3}.");

        // 1/sqrt(sigma): four decades of sigma must buy exactly two decades of defect.
        double ratio = Defect(1e12) / Defect(1e16);
        Assert.True(ratio > 90.0 && ratio < 110.0,
            $"The residual defect scales by {ratio:F1}x over four decades of sigma, not the ~100x that " +
            $"skin-effect resistance would. Something other than the metal is dissipating.");
    }

    // ---------------------------------------------------------------- 6.5

    /// <summary>
    /// The <b>network</b> converges under refinement — which is a different question from WM-1 §9.7's
    /// single-wire capacitance, and the one that decides whether 24 segments is the right default.
    ///
    /// <para>8 wires in 2 arrays at 12 / 24 / 48 segments (N_s ≤ 384), three frequencies. The 48↔24
    /// change must be smaller than the 24↔12 one at every frequency. All three deltas are in
    /// <c>RESOLVED.md</c>.</para>
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(10e9)]
    [InlineData(40e9)]
    public void TheNetworkConverges_AndTwentyFourSegmentsIsPastTheKnee(double frequencyHz)
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 8, arrayCount: 2, pointsPerWire: 7);
        var z0 = new Complex(50.0, 0.0);

        Mat<Complex> S(int segments)
        {
            var solver = Solver(design, segments);
            return RFNetwork.ZToS(ToMat(solver.PortImpedance(frequencyHz), solver.TerminalCount), z0);
        }

        var s12 = S(12);
        var s24 = S(24);
        var s48 = S(48);

        static double MaxDelta(Mat<Complex> a, Mat<Complex> b)
        {
            double worst = 0.0;
            for (int i = 0; i < a.RowCount; i++)
                for (int j = 0; j < a.ColCount; j++)
                    worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude);
            return worst;
        }

        double coarse = MaxDelta(s24, s12);
        double fine = MaxDelta(s48, s24);

        Assert.True(fine < coarse,
            $"At {frequencyHz * 1e-9:0.##} GHz: |S(48)−S(24)| = {fine:E3} is not below " +
            $"|S(24)−S(12)| = {coarse:E3}.");
    }

    // ---------------------------------------------------------------- 6.7

    /// <summary>
    /// Below <see cref="WireMomSettings.MinimumFrequencyHz"/> the solver refuses, and the message
    /// carries the measured number and names the model to use instead.
    /// </summary>
    [Fact]
    public void BelowTheMeasuredFloor_TheSolverRefuses_AndNamesTheAnalyticModel()
    {
        var solver = Solver(FourWireTwoArray());

        var ex = Assert.Throws<InvalidOperationException>(() => solver.PortImpedance(1e4));

        Assert.Contains("1E+5", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lumped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The series arm has <b>no</b> such floor — it never forms <c>K̃</c>, so there is nothing to be
    /// ill-conditioned. Refusing there would be refusing the one thing that still works.
    /// </summary>
    [Fact]
    public void TheSeriesArm_HasNoLowFrequencyFloor()
    {
        var design = FourWireTwoArray();
        var mom = Solver(design).SeriesArmImpedance(1e3);
        var analytic = ImpedanceReduction.Create(design).ArrayImpedance(1e3);

        Assert.True(WorstRelative(mom, analytic) < 1e-10);
    }

    /// <summary>
    /// A design with no ground plane is refused at <b>mesh</b> time (WM-1 §3.4 / RW13), and the solver
    /// surfaces that refusal rather than failing later with something else.
    /// </summary>
    [Fact]
    public void WithNoGroundPlane_TheSolverSurfacesTheMeshRefusal()
    {
        var design = FourWireTwoArray();
        design.GroundPlane.Enabled = false;

        var ex = Assert.Throws<InvalidOperationException>(() => WireMomSolver.Create(design));
        Assert.Contains("ground plane", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- §4 notes

    /// <summary>
    /// The result carries its caveats, and the quasi-static one carries its two numbers. A caveat
    /// nobody can act on is decoration; λ/10 at the top frequency and the design's own widest pair
    /// separation are what make it actionable.
    /// </summary>
    [Fact]
    public void TheResultCarriesTheQuasiStaticNote_WithBothNumbersInIt()
    {
        var result = Solver(FourWireTwoArray()).Solve([1e9, 40e9]);

        Assert.Equal(2, result.Frequencies.Count);
        var note = Assert.Single(result.Notes, n => n.Contains("Quasi-static", StringComparison.Ordinal));

        Assert.Contains("mm at 40 GHz", note, StringComparison.Ordinal);
        Assert.Contains("widest wire-pair separation", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>IncludeCapacitance = false</c> has no meaning for this kernel — the MoM network <i>is</i> the
    /// coupled L–C ladder. It is neither refused nor silently obeyed: the capacitance is included and
    /// the result says so.
    /// </summary>
    [Fact]
    public void WithIncludeCapacitanceOff_TheResultSaysTheSettingDoesNotApply()
    {
        var design = FourWireTwoArray();
        design.IncludeCapacitance = false;

        var result = Solver(design).Solve([1e9]);

        Assert.Contains(result.Notes, n => n.Contains("intrinsic to the distributed model", StringComparison.Ordinal));
    }
}
