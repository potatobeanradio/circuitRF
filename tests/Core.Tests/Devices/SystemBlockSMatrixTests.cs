using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The three blocks brief-sys-3 adds to the ideal-S family — circulator, directional coupler (which
/// is also both hybrids) and balun — at the model level: the S-matrix each set of parameters
/// produces, entry by entry.
///
/// <para>Every expected number is computed HERE from the dB and degree values, never read back out
/// of the model. What needs gating is the trip from "20 dB of coupling at 90°" to a complex
/// amplitude, and no comparison of the model with itself can make it.</para>
///
/// <para>The end-to-end half — a swept solve returning exactly this S, the measured
/// non-reciprocity, the circulator's Y and the back-to-back hybrid identity — lives in
/// <c>tests/Engine.Tests/Devices/SystemBlockSParamTests.cs</c>, because it needs an engine.</para>
/// </summary>
public class SystemBlockSMatrixTests
{
    private static double Amp(double db)  => Math.Pow(10.0, -db / 20.0);

    /// <summary>
    /// Degrees to radians. The models take an ANGLE in radians because the Elaborator has already
    /// applied the parameter's own <c>deg</c> unit by the time the factory runs — the convention
    /// <c>TLineModel</c>'s <c>E</c> established. The theory data below is in degrees because that
    /// is what a user types on the schematic, and this is the one conversion between them.
    /// </summary>
    private static double Rad(double deg) => deg * Math.PI / 180.0;
    private static double Supp(double db) => db >= 150.0 ? 0.0 : Amp(db);

    private static void Near(Complex expected, Complex actual, double tol = 1e-12)
        => Assert.True((expected - actual).Magnitude < tol,
                       $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Magnitude:G6})");

    // ══ Circulator ════════════════════════════════════════════════════════════

    [Fact]
    public void Circulator_AtItsDefaults_IsThePermutationMatrix()
    {
        // CW is 1→2, 2→3, 3→1, so the ONLY non-zero entries are S21, S32, S13 — and they are
        // exactly 1, with nothing at all stamped anywhere else. That matrix is why this family
        // exists: it has no Z form (see the next test).
        var s = new CirculatorModel(CirculatorDirection.CW, 0, 200, 200, 50).SAt(2 * Math.PI * 1e9);

        var expected = new Complex[3, 3];
        expected[1, 0] = expected[2, 1] = expected[0, 2] = Complex.One;

        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            Assert.Equal(expected[p, q], s[p, q]);   // exact, not near — an absent term is absent
    }

    [Fact]
    public void Circulator_TheIdealMatrixHasNoZForm_AndItsYIsTheAntisymmetricOne()
    {
        // det(I − S) = 0 EXACTLY, so Z = Z0(I+S)(I−S)⁻¹ does not exist — no tolerance, no
        // near-singularity, no conditioning argument. This is the executable form of the sentence in
        // the model's doc comment, so the next reader who wonders why the repository grew a third
        // N-port stamp finds the answer rather than only the claim.
        var s = new CirculatorModel(CirculatorDirection.CW, 0, 200, 200, 50).SAt(0.0);

        Assert.Equal(0.0, Det3(Minus(Eye(), s)).Magnitude, 15);

        // Its Y does exist: det(I + S) = 2, and Y = (1/Z0)(I − S)(I + S)⁻¹ is antisymmetric with a
        // zero diagonal — itself singular, because every row and column sums to zero as a floating
        // network's must. SYS-4's memoryless overlay needs exactly this matrix.
        Assert.Equal(2.0, Det3(Plus(Eye(), s)).Real, 12);

        var y = YFromS(s, 50.0);
        var expected = new[,] { { 0.0, 1.0, -1.0 }, { -1.0, 0.0, 1.0 }, { 1.0, -1.0, 0.0 } };
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            Near(new Complex(expected[p, q] / 50.0, 0), y[p, q], 1e-14);
    }

    [Theory]
    [InlineData(CirculatorDirection.CW,  0.0, 200.0, 200.0)]
    [InlineData(CirculatorDirection.CCW, 0.0, 200.0, 200.0)]
    [InlineData(CirculatorDirection.CW,  0.4,  20.0,  18.0)]
    [InlineData(CirculatorDirection.CCW, 0.4,  20.0,  18.0)]
    [InlineData(CirculatorDirection.CW,  1.2,  35.0,  25.0)]
    public void Circulator_TheWholeMatrix(CirculatorDirection dir, double il, double iso, double rl)
    {
        var s = new CirculatorModel(dir, il, iso, rl, 50).SAt(2 * Math.PI * 3e9);

        double fwd = Amp(il), rev = Supp(iso), refl = Supp(rl);
        bool   cw  = dir == CirculatorDirection.CW;

        for (int p = 0; p < 3; p++)
        {
            Near(new Complex(refl, 0), s[p, p]);
            int next = (p + 1) % 3;
            Near(new Complex(cw ? fwd : rev, 0), s[next, p]);
            Near(new Complex(cw ? rev : fwd, 0), s[p, next]);
        }
    }

    [Fact]
    public void Circulator_ReversingTheDirectionTransposesTheMatrixAndNothingElse()
    {
        var cw  = (Complex[,])new CirculatorModel(CirculatorDirection.CW,  0.4, 20, 18, 50).SAt(0.0).Clone();
        var ccw = new CirculatorModel(CirculatorDirection.CCW, 0.4, 20, 18, 50).SAt(0.0);

        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            Assert.Equal(cw[q, p], ccw[p, q]);

        // and it is a real move, not two copies of a symmetric matrix: S ≠ Sᵀ is the entire point of
        // the component and no other passive in the repository has it.
        Assert.NotEqual(cw[0, 1], cw[1, 0]);
    }

    [Fact]
    public void Circulator_IsThreePorts_Linear_AndNumbersItsTerminalsRoundTheCircle()
    {
        var m = new CirculatorModel(CirculatorDirection.CW, 0, 200, 200, 50);
        Assert.Equal(3, m.PortCount);
        Assert.Equal(ModelKind.Linear, m.Kind);
        Assert.Equal(["1", "2", "3"], m.TerminalNames);
        Assert.Equal(50.0, m.Z0Of(2));
    }

    // ══ Coupler, and both hybrids ═════════════════════════════════════════════

    [Theory]
    [InlineData(20.0,      90.0, 200.0, 0.0, 200.0)]   // the coupler's own defaults
    [InlineData(3.0103,    90.0, 200.0, 0.0, 200.0)]   // Hybrid90's
    [InlineData(3.0103,   180.0, 200.0, 0.0, 200.0)]   // Hybrid180's
    [InlineData(10.0,       0.0,  25.0, 0.5,  20.0)]   // and three non-ideal settings
    [InlineData(6.0,       90.0,  18.0, 1.0,  15.0)]
    [InlineData(30.0,     180.0,  30.0, 0.2,  22.0)]
    public void Coupler_TheWholeMatrix(double coupling, double phase, double directivity,
                                       double il, double rl)
    {
        var s = new CouplerModel(coupling, Rad(phase), directivity, il, rl, 50).SAt(2 * Math.PI * 2e9);

        double  c    = Amp(coupling);
        double  loss = Amp(il);
        Complex thru = new(Math.Sqrt(1.0 - c * c) * loss, 0);
        Complex cpl  = Complex.FromPolarCoordinates(c * loss, -Rad(phase));
        Complex iso  = new(c * Supp(directivity) * loss, 0);
        Complex refl = new(Supp(rl), 0);

        var expected = new Complex[4, 4];
        for (int p = 0; p < 4; p++) expected[p, p] = refl;
        expected[0, 1] = expected[1, 0] = expected[2, 3] = expected[3, 2] = thru;
        expected[0, 2] = expected[2, 0] = expected[1, 3] = expected[3, 1] = cpl;
        expected[0, 3] = expected[3, 0] = expected[1, 2] = expected[2, 1] = iso;

        for (int p = 0; p < 4; p++)
        for (int q = 0; q < 4; q++)
            Near(expected[p, q], s[p, q]);
    }

    [Theory]
    [InlineData(3.0103)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(0.5)]
    public void Coupler_TheIdealSplitIsSetByCouplingAlone_AndIsLossless(double couplingDb)
    {
        // |S21|² + |S31|² = 1: the through arm gets exactly what the coupled arm did not take, so a
        // 20 dB coupler's 0.0436 dB of main-arm loss comes out of the arithmetic rather than out of
        // a parameter. IL is a loss ADDED on top of this, which is why it is zero here.
        var s = new CouplerModel(couplingDb, Rad(90), 200, 0, 200, 50).SAt(0.0);

        double thru = s[1, 0].Magnitude, cpl = s[2, 0].Magnitude;
        Assert.Equal(1.0, thru * thru + cpl * cpl, 12);
        Assert.Equal(Amp(couplingDb), cpl, 12);

        // and the isolated port is EXACTLY zero — no entry stamped, not 1e-10 of one.
        Assert.Equal(Complex.Zero, s[3, 0]);
        Assert.Equal(Complex.Zero, s[0, 3]);
        Assert.Equal(Complex.Zero, s[1, 2]);
        Assert.Equal(Complex.Zero, s[2, 1]);
    }

    [Fact]
    public void Coupler_AtThreeDbAndNinetyDegrees_IsTheQuadratureHybrid()
    {
        // 3.0103 dB is the equal split to the precision the number carries, and the coupled arm
        // lags the through arm by exactly 90°.
        var s = new CouplerModel(3.0103, Rad(90), 200, 0, 200, 50).SAt(2 * Math.PI * 1e9);

        Assert.Equal(1.0 / Math.Sqrt(2.0), s[1, 0].Magnitude, 8);
        Assert.Equal(1.0 / Math.Sqrt(2.0), s[2, 0].Magnitude, 8);
        Assert.Equal(-90.0, (s[2, 0].Phase - s[1, 0].Phase) * 180.0 / Math.PI, 12);
    }

    [Fact]
    public void Coupler_TheQuadratureCaseIsUnitary_AndTheZeroAndOneEightyOnesAreNot()
    {
        // Worth an executable statement because it is a THEOREM rather than a modelling slip: a
        // lossless, matched, reciprocal four-port with directivity must have its coupled arm in
        // quadrature with its through arm. At 0° or 180° each ROW of the matrix is still of unit
        // norm — the block is energy-consistent under any single-port excitation — but rows 1 and 4
        // stop being orthogonal, so it is not simultaneously realisable. It is stamped anyway.
        Assert.True(RowsOrthonormal(new CouplerModel(3.0103, Rad(90), 200, 0, 200, 50).SAt(0.0)));

        foreach (double phase in new[] { 0.0, 180.0 })
        {
            var s = new CouplerModel(3.0103, Rad(phase), 200, 0, 200, 50).SAt(0.0);
            Assert.False(RowsOrthonormal(s), $"{phase}° should not be unitary");

            for (int p = 0; p < 4; p++)     // each row is still of unit norm
            {
                double n2 = 0;
                for (int q = 0; q < 4; q++) n2 += s[p, q].Magnitude * s[p, q].Magnitude;
                Assert.Equal(1.0, n2, 8);
            }
        }
    }

    [Fact]
    public void Coupler_ACouplingAboveZeroDbIsStamped_AsAnImaginaryThroughRatherThanANaN()
    {
        // A user is allowed to type numbers a physical part could not have; this model refuses only
        // what cannot be stamped. t = √(1 − c²) with c > 1 is honestly imaginary, and a NaN here
        // would surface as a non-convergence with nothing attached to it.
        var s = new CouplerModel(-6.0, Rad(90), 200, 0, 200, 50).SAt(0.0);
        double c = Amp(-6.0);

        Assert.False(double.IsNaN(s[1, 0].Real) || double.IsNaN(s[1, 0].Imaginary));
        Near(new Complex(0, Math.Sqrt(c * c - 1.0)), s[1, 0]);
        Near(new Complex(0, -c), s[2, 0]);
    }

    [Fact]
    public void Coupler_InsertionLossScalesAllThreePaths_SoDirectivityKeepsItsMeaning()
    {
        const double d = 25.0, il = 2.0;
        var s = new CouplerModel(10, Rad(90), d, il, 200, 50).SAt(0.0);

        // The isolated port sits `Directivity` dB below the COUPLED port whatever the insertion
        // loss is. Scaling only the through and coupled arms would have quietly turned a 25 dB
        // directivity into a 23 dB one.
        Assert.Equal(d, 20.0 * Math.Log10(s[2, 0].Magnitude / s[3, 0].Magnitude), 12);
        Assert.Equal(Amp(il), s[2, 0].Magnitude / Amp(10.0), 12);
    }

    [Fact]
    public void Coupler_IsFourPorts_Linear_AndNamesItsTerminalsAfterWhatTheyDo()
    {
        var m = new CouplerModel(20, Rad(90), 200, 0, 200, 75);
        Assert.Equal(4, m.PortCount);
        Assert.Equal(ModelKind.Linear, m.Kind);
        Assert.Equal(["in", "thru", "cpl", "iso"], m.TerminalNames);
        for (int p = 0; p < 4; p++) Assert.Equal(75.0, m.Z0Of(p));
    }

    [Fact]
    public void Coupler_AtNinetyDegrees_ObeysTheConjugateRule()
    {
        // The first block in this family with a genuinely complex S, which is what
        // IdealSBlockModel's S(−ω) = conj(S(ω)) rule was written for. HB does not currently hand a
        // model a negative ω, so this stamps at one directly.
        var m   = new CouplerModel(3.0103, Rad(90), 200, 0, 200, 50);
        var pos = (Complex[,])m.SAt(+2 * Math.PI * 1e9).Clone();
        var neg = m.SAt(-2 * Math.PI * 1e9);

        for (int p = 0; p < 4; p++)
        for (int q = 0; q < 4; q++)
            Assert.Equal(Complex.Conjugate(pos[p, q]), neg[p, q]);

        Assert.NotEqual(pos[2, 0], neg[2, 0]);   // and it actually moved
    }

    // ══ Balun ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Balun_AtItsDefaults_SplitsInHalfAndExactlyAntiphase()
    {
        var s = new BalunModel(50, 50, 0, 0, 0).SAt(2 * Math.PI * 1e9);
        double half = 1.0 / Math.Sqrt(2.0);

        // EXACTLY antiphase: equal magnitudes and a zero imaginary part on both, which is why the
        // 180° lives in the sign rather than in an exponent that would leave a 1.2e−16 residue.
        Assert.Equal(new Complex(+half, 0), s[1, 0]);
        Assert.Equal(new Complex(-half, 0), s[2, 0]);
        Assert.Equal(s[1, 0].Magnitude, s[2, 0].Magnitude, 15);
        Assert.Equal(0.0, s[2, 0].Imaginary);

        Assert.Equal(Complex.Zero, s[0, 0]);                  // the unbalanced port is matched
        foreach (var (p, q) in new[] { (1, 1), (2, 2), (1, 2), (2, 1) })
            Assert.Equal(new Complex(0.5, 0), s[p, q]);       // and the balanced pair is not
    }

    [Theory]
    [InlineData(0.0,  0.0,  0.0)]
    [InlineData(0.0,  0.8,  0.0)]
    [InlineData(0.0,  0.0,  6.0)]
    [InlineData(1.5,  0.8,  6.0)]
    [InlineData(0.5, -1.2, -4.0)]
    public void Balun_TheWholeMatrix(double il, double ampImb, double phaseImb)
    {
        var s = new BalunModel(50, 50, il, ampImb, Rad(phaseImb)).SAt(0.0);

        double  loss = Amp(il);
        double  k    = Math.Pow(10.0, ampImb / 40.0);
        double  half = 1.0 / Math.Sqrt(2.0);
        Complex plus  = new(half * k * loss, 0);
        Complex minus = -Complex.FromPolarCoordinates(half / k * loss, -Rad(phaseImb));

        Near(plus,  s[1, 0]); Near(plus,  s[0, 1]);
        Near(minus, s[2, 0]); Near(minus, s[0, 2]);
        Near(Complex.Zero, s[0, 0]);
        foreach (var (p, q) in new[] { (1, 1), (2, 2), (1, 2), (2, 1) })
            Near(new Complex(0.5, 0), s[p, q]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.8)]
    [InlineData(3.0)]
    [InlineData(-1.5)]
    public void Balun_AmplitudeImbalanceIsTheDbGapBetweenTheOutputs_SplitSymmetrically(double ampImb)
    {
        var s = new BalunModel(50, 50, 0, ampImb, 0).SAt(0.0);

        // The parameter names the GAP, and the model splits it half up and half down, so the mean
        // of the two output levels in dB is unchanged.
        Assert.Equal(ampImb, 20.0 * Math.Log10(s[1, 0].Magnitude / s[2, 0].Magnitude), 12);
        Assert.Equal(0.0,
                     10.0 * Math.Log10(s[1, 0].Magnitude) + 10.0 * Math.Log10(s[2, 0].Magnitude)
                   - 20.0 * Math.Log10(1.0 / Math.Sqrt(2.0)), 12);
    }

    [Theory]
    [InlineData(0.0,   180.0)]
    [InlineData(6.0,   174.0)]
    [InlineData(-4.0,  184.0)]
    public void Balun_PhaseImbalanceIsTheDepartureFromOneEighty(double phaseImb, double expectedDeg)
    {
        var s = new BalunModel(50, 50, 0, 0, Rad(phaseImb)).SAt(0.0);
        double deg = (s[2, 0].Phase - s[1, 0].Phase) * 180.0 / Math.PI;
        if (deg < 0) deg += 360.0;
        Assert.Equal(expectedDeg, deg, 12);
    }

    [Fact]
    public void Balun_InTheModalBasis_IsAnIdealThroughAndACommonModeOpen()
    {
        // The 1/2 block at ports 2 and 3 is not a mistake, and this is what it says: with
        // d = (2 − 3)/√2 and c = (2 + 3)/√2 the ideal matrix is [[0,1,0],[1,0,0],[0,0,1]] — an
        // ideal through from the unbalanced port to the DIFFERENTIAL mode, and a total reflection
        // for the COMMON mode. A lossless reciprocal three-port cannot have all three ports
        // matched, and a real balun does not isolate its balanced ports from each other either.
        var s = new BalunModel(50, 50, 0, 0, 0).SAt(0.0);
        double r2 = Math.Sqrt(2.0);

        Near(Complex.Zero, s[0, 0]);                                 // unb ↔ unb
        Near(Complex.One,  (s[0, 1] - s[0, 2]) / r2);                 // unb ↔ differential
        Near(Complex.Zero, (s[0, 1] + s[0, 2]) / r2);                 // unb ↔ common
        Near(Complex.Zero, (s[1, 1] - s[1, 2] - s[2, 1] + s[2, 2]) / 2);   // d ↔ d
        Near(Complex.One,  (s[1, 1] + s[1, 2] + s[2, 1] + s[2, 2]) / 2);   // c ↔ c
        Near(Complex.Zero, (s[1, 1] + s[1, 2] - s[2, 1] - s[2, 2]) / 2);   // d ↔ c
    }

    [Theory]
    [InlineData(50.0, 50.0)]
    [InlineData(50.0, 25.0)]
    [InlineData(75.0, 50.0)]
    public void Balun_TakesItsBalancedReferenceImpedancePerPort(double zUnb, double zBal)
    {
        var m = new BalunModel(zUnb, zBal, 0, 0, 0);
        Assert.Equal(3, m.PortCount);
        Assert.Equal(ModelKind.Linear, m.Kind);
        Assert.Equal(["unb", "bal+", "bal-"], m.TerminalNames);
        Assert.Equal(zUnb, m.Z0Of(0));
        Assert.Equal(zBal, m.Z0Of(1));
        Assert.Equal(zBal, m.Z0Of(2));
    }

    // ── small dense helpers, so the gate does its own arithmetic ──────────────

    private static Complex[,] Eye()
    {
        var m = new Complex[3, 3];
        for (int i = 0; i < 3; i++) m[i, i] = Complex.One;
        return m;
    }

    private static Complex[,] Plus(Complex[,] a, Complex[,] b)  => Combine(a, b, +1);
    private static Complex[,] Minus(Complex[,] a, Complex[,] b) => Combine(a, b, -1);

    private static Complex[,] Combine(Complex[,] a, Complex[,] b, double sign)
    {
        var m = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            m[p, q] = a[p, q] + sign * b[p, q];
        return m;
    }

    private static Complex Det3(Complex[,] m)
        => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
         - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
         + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    /// <summary>Y = (1/Z0)·(I − S)(I + S)⁻¹ for a uniform real reference impedance.</summary>
    private static Complex[,] YFromS(Complex[,] s, double z0)
    {
        var inv = Invert3(Plus(Eye(), s));
        var num = Minus(Eye(), s);
        var y   = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
        {
            Complex acc = Complex.Zero;
            for (int k = 0; k < 3; k++) acc += num[p, k] * inv[k, q];
            y[p, q] = acc / z0;
        }
        return y;
    }

    private static Complex[,] Invert3(Complex[,] m)
    {
        Complex det = Det3(m);
        var inv = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
        {
            // Cofactor of (q,p) — the adjugate is the transpose of the cofactor matrix.
            int r0 = (q + 1) % 3, r1 = (q + 2) % 3;
            int c0 = (p + 1) % 3, c1 = (p + 2) % 3;
            inv[p, q] = (m[r0, c0] * m[r1, c1] - m[r0, c1] * m[r1, c0]) / det;
        }
        return inv;
    }

    private static bool RowsOrthonormal(Complex[,] s)
    {
        int n = s.GetLength(0);
        for (int p = 0; p < n; p++)
        for (int q = 0; q < n; q++)
        {
            Complex acc = Complex.Zero;
            for (int k = 0; k < n; k++) acc += s[p, k] * Complex.Conjugate(s[q, k]);
            Complex want = p == q ? Complex.One : Complex.Zero;
            if ((acc - want).Magnitude > 1e-8) return false;
        }
        return true;
    }
}
