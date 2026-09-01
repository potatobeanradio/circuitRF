using System.Numerics;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The ideal filter's polynomial core (brief-sys-6, milestones 1 and 2): the five response
/// families, the frequency transformations, and the limits an ordinary sweep reaches. No simulator
/// in the loop — every expectation here is computed from the TEXTBOOK formula, in trigonometric
/// form, and never from the polynomial machinery under test.
///
/// <para><b>Why the expectations are written with <c>cos</c> and <c>cosh</c> rather than with a
/// polynomial.</b> <see cref="MatchPoly.ChebyshevT"/> is the three-term recurrence the production
/// code itself builds the family from; asserting against it would compare the code with its own
/// input. <c>T_n(x) = cos(n·acos x)</c> inside the band and <c>cosh(n·acosh x)</c> outside it is a
/// different route to the same number, and it is the one a reader can check by hand.</para>
///
/// <para>The end-to-end half — a swept solve returning exactly this response, the group delay
/// measured off simulated phase, the unequal-impedance case and the duplexer — lives in
/// <c>tests/Engine.Tests/Devices/FilterSParamTests.cs</c>.</para>
/// </summary>
public class FilterPrototypeTests(ITestOutputHelper output)
{
    /// <summary>The Chebyshev polynomial in its trigonometric form, valid on both sides of ±1.</summary>
    private static double T(int n, double x)
        => Math.Abs(x) <= 1.0
            ? Math.Cos(n * Math.Acos(x))
            : (x < 0 && n % 2 == 1 ? -1.0 : 1.0) * Math.Cosh(n * Math.Acosh(Math.Abs(x)));

    private static double Db(Complex z) => 20.0 * Math.Log10(z.Magnitude);

    /// <summary>Fifteen frequencies spanning three decades either side of the prototype edge.</summary>
    private static readonly double[] Fifteen =
        [0.0, 0.1, 0.25, 0.4, 0.6, 0.75, 0.9, 1.0, 1.1, 1.3, 1.7, 2.5, 4.0, 8.0, 25.0];

    // ══ Butterworth ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Butterworth_IsTheMaximallyFlatMagnitude(int n)
    {
        var p = FilterPrototype.Create(FilterResponse.Butterworth, n);

        foreach (double w in Fifteen)
        {
            // The definition, with no ε and no shape parameter: ω^2n against 1.
            double expected = 1.0 / (1.0 + Math.Pow(w, 2 * n));
            var (_, s21, _) = p.At(w);
            Assert.True(Math.Abs(s21.Magnitude * s21.Magnitude - expected) < 1e-10,
                $"n = {n}, ω = {w}: |S21|² = {s21.Magnitude * s21.Magnitude:G17}, expected {expected:G17}");
        }

        // …and the 3.0103 dB point is at the edge, which is what "cutoff" means for this family.
        Assert.Equal(-10.0 * Math.Log10(2.0), Db(p.At(1.0).S21), 10);
    }

    // ══ Chebyshev ═════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1, 0.01)] [InlineData(2, 0.1)] [InlineData(3, 0.1)] [InlineData(4, 0.5)]
    [InlineData(5, 0.1)]  [InlineData(6, 1.0)] [InlineData(7, 0.05)]
    public void Chebyshev_IsTheEquirippleMagnitude(int n, double rippleDb)
    {
        var p = FilterPrototype.Create(FilterResponse.Chebyshev, n, rippleDb);
        double eps = Math.Sqrt(Math.Pow(10.0, rippleDb / 10.0) - 1.0);

        foreach (double w in Fifteen)
        {
            double tn = T(n, w);
            double expected = 1.0 / (1.0 + eps * eps * tn * tn);
            var (_, s21, _) = p.At(w);
            Assert.True(Math.Abs(s21.Magnitude * s21.Magnitude - expected) < 1e-10,
                $"n = {n}, ripple = {rippleDb} dB, ω = {w}: |S21|² = {s21.Magnitude * s21.Magnitude:G17}, " +
                $"expected {expected:G17}");
        }
    }

    /// <summary>
    /// The STRUCTURAL claim, which the closed form above does not make: the passband swings between
    /// 0 dB and exactly −Ripple, and the number of swings is what the order buys.
    /// </summary>
    /// <remarks>
    /// <b>brief-sys-6 asks for "exactly n ripple extrema"; the honest count is TWO numbers, and
    /// neither of them is n on its own.</b> Over the whole prototype passband <c>[−1, 1]</c> — which
    /// is what a bandpass maps onto its band, so it is what a user sees on a plot — there are
    /// exactly <c>n</c> points where the response touches 0 dB (the reflection zeros, where
    /// <c>T_n = 0</c>) and exactly <c>n + 1</c> where it touches <c>−Ripple</c> (where
    /// <c>|T_n| = 1</c>), two of which are the band edges themselves. The interior turning points
    /// therefore number <c>2n − 1</c>. "n ripples" is the count of reflection zeros, and it is the
    /// one asserted first below; the other two follow from it and are asserted so the arithmetic is
    /// visible rather than folded into one number that could be right for the wrong reason.
    ///
    /// <para>Each extremum's VALUE is checked against the level the ripple figure names, so a
    /// response with the right number of wiggles at the wrong depth fails.</para>
    /// </remarks>
    [Theory]
    [InlineData(2, 0.1)] [InlineData(3, 0.1)] [InlineData(4, 0.5)]
    [InlineData(5, 0.01)] [InlineData(6, 1.0)] [InlineData(7, 0.1)]
    public void Chebyshev_PassbandRipplesExactlyNTimes_EachTouchingTheStatedDepth(int n, double rippleDb)
    {
        var p = FilterPrototype.Create(FilterResponse.Chebyshev, n, rippleDb);

        const int Points = 200001;
        var db = new double[Points];
        for (int i = 0; i < Points; i++)
            db[i] = Db(p.At(-1.0 + 2.0 * i / (Points - 1.0)).S21);

        int maxima = 0, minima = 0;
        for (int i = 1; i < Points - 1; i++)
        {
            if (db[i] > db[i - 1] && db[i] >= db[i + 1]) { maxima++; Assert.Equal(0.0, db[i], 5); }
            if (db[i] < db[i - 1] && db[i] <= db[i + 1]) { minima++; Assert.Equal(-rippleDb, db[i], 5); }
        }

        Assert.Equal(n, maxima);                       // the n reflection zeros — "n ripples"
        Assert.Equal(n - 1, minima);                   // interior ripple floors
        Assert.Equal(2 * n - 1, maxima + minima);      // …and the two band edges make n + 1 floors
        Assert.Equal(-rippleDb, db[0], 5);
        Assert.Equal(-rippleDb, db[^1], 5);
    }

    // ══ Inverse Chebyshev ═════════════════════════════════════════════════════

    [Theory]
    [InlineData(2, 20.0)] [InlineData(3, 40.0)] [InlineData(4, 40.0)]
    [InlineData(5, 60.0)] [InlineData(6, 30.0)] [InlineData(7, 80.0)]
    public void InvChebyshev_HasAFlatPassbandAndAnEquirippleStopbandAtAstop(int n, double astopDb)
    {
        var p = FilterPrototype.Create(FilterResponse.InvChebyshev, n, 0.1, astopDb);
        double eps = Math.Sqrt(Math.Pow(10.0, astopDb / 10.0) - 1.0);

        // The closed form: C_n(ω) = 1/T_n(1/ω), so |S21|² = 1/(1 + ε²/T_n(1/ω)²).
        foreach (double w in Fifteen.Where(x => x > 0.0))
        {
            double tn = T(n, 1.0 / w);
            double expected = 1.0 / (1.0 + eps * eps / (tn * tn));
            var (_, s21, _) = p.At(w);
            Assert.True(Math.Abs(s21.Magnitude * s21.Magnitude - expected) < 1e-10,
                $"n = {n}, Astop = {astopDb} dB, ω = {w}: |S21|² = {s21.Magnitude * s21.Magnitude:G17}, " +
                $"expected {expected:G17}");
        }

        // Ω = 1 is the STOPBAND edge for this family — T_n(1) = 1, so the response is exactly at
        // the floor there. That is a different meaning of Fc from the other four families, and the
        // parameter description says so.
        Assert.Equal(-astopDb, Db(p.At(1.0).S21), 9);

        // The stopband is equiripple AT the floor: every peak beyond the edge touches it and none
        // rises above it.
        double peak = double.NegativeInfinity;
        int touches = 0;
        double prev = double.NegativeInfinity, prevPrev = double.NegativeInfinity;
        for (int i = 0; i <= 400000; i++)
        {
            double d = Db(p.At(1.0 + i * 0.0005).S21);
            peak = Math.Max(peak, d);
            if (i >= 2 && prev > prevPrev && prev >= d) touches++;
            prevPrev = prev; prev = d;
        }
        Assert.True(peak <= -astopDb + 1e-6, $"stopband rose to {peak:F9} dB, above the {-astopDb} dB floor");

        // Interior peaks in the stopband: one per finite transmission-zero PAIR beyond the edge.
        // n/2 for even n; (n−1)/2 for odd n, whose last zero has gone to infinity.
        Assert.Equal(n / 2 - (n % 2 == 0 ? 1 : 0), touches);

        // The passband is maximally flat: |S21| at DC is exactly 1, and the first n−1 derivatives
        // vanish there, which shows as a response that has not moved measurably at ω = 0.1.
        Assert.Equal(1.0, p.At(0.0).S21.Magnitude, 12);
        Assert.Equal(0.0, p.At(0.0).S11.Magnitude, 12);
    }

    // ══ Bessel ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bessel's gate is its GROUP DELAY, not its magnitude. Its <c>|S21|</c> is neither equiripple
    /// nor maximally flat and asserting anything about it would be asserting the wrong property of
    /// the right filter.
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Bessel_HasUnitGroupDelayAtDc_MaximallyFlatAndThenMonotone(int n)
    {
        var p = FilterPrototype.Create(FilterResponse.Bessel, n);

        double Phase(double w) => p.At(w).S21.Phase;
        double Tau(double w, double h) => -(Phase(w + h) - Phase(w - h)) / (2.0 * h);

        // Unit delay at DC — the normalisation that makes ω_c mean 1/τ for this family.
        Assert.Equal(1.0, Tau(0.0, 1e-4), 8);

        // …and monotone from there on: the delay falls, and never rises again.
        double prev = double.PositiveInfinity;
        for (double w = 0.0; w <= 12.0; w += 0.02)
        {
            double tau = Tau(w, 1e-4);
            Assert.True(tau <= prev + 1e-7, $"n = {n}: group delay rose at ω = {w:F3} ({prev:G6} → {tau:G6})");
            prev = tau;
        }

        output.WriteLine($"Bessel n = {n}: τ(0) = {Tau(0, 1e-4):F12}, τ(1) = {Tau(1, 1e-4):F6}, " +
                         $"|S21(1)| = {Db(p.At(1.0).S21):F4} dB");
    }

    /// <summary>
    /// "Maximally flat group delay" as a measurement rather than as a tolerance: the band over which
    /// the delay stays within 1% of its DC value GROWS with every order. That is the whole reason to
    /// pick this family, and it is a property no single-order assertion can state.
    /// </summary>
    /// <remarks>
    /// Written as a comparison across orders because a fixed tolerance at a fixed frequency is the
    /// wrong gate here: the delay error goes as ω^2n, so any one number is slack at high order and
    /// impossible at n = 1 — where "maximally flat" buys exactly one vanishing derivative and
    /// τ = 1/(1 + ω²) is already 2% down at a sixth of the corner.
    /// </remarks>
    [Fact]
    public void Bessel_TheFlatDelayBandWidensWithEveryOrder()
    {
        double OnePercentBand(int n)
        {
            var p = FilterPrototype.Create(FilterResponse.Bessel, n);
            double Tau(double w) => -(p.At(w + 1e-4).S21.Phase - p.At(w - 1e-4).S21.Phase) / 2e-4;
            for (double w = 0.0; w <= 20.0; w += 0.001)
                if (Tau(w) < 0.99) return w;
            return double.PositiveInfinity;
        }

        double prev = 0.0;
        for (int n = 1; n <= 7; n++)
        {
            double band = OnePercentBand(n);
            output.WriteLine($"Bessel n = {n}: group delay within 1% of DC out to ω = {band:F3}");
            Assert.True(band > prev, $"n = {n} was not flatter than n = {n - 1} ({band:F3} vs {prev:F3})");
            prev = band;
        }
    }

    // ══ Elliptic ══════════════════════════════════════════════════════════════

    /// <summary>
    /// The elliptic family, which is the only one needing mathematics the repository did not have.
    /// It is gated on the two properties that DEFINE it and that nothing else in the file shares:
    /// both bands equiripple, at exactly the two stated levels.
    /// </summary>
    [Theory]
    [InlineData(2, 0.1,  40.0)] [InlineData(3, 0.1,  40.0)] [InlineData(4, 0.5, 60.0)]
    [InlineData(5, 0.01, 30.0)] [InlineData(6, 0.25, 50.0)] [InlineData(7, 0.1, 80.0)]
    public void Elliptic_IsEquirippleInBothBands_AtExactlyTheStatedLevels(int n, double rippleDb, double astopDb)
    {
        var p = FilterPrototype.Create(FilterResponse.Elliptic, n, rippleDb, astopDb);

        // Passband: swings between 0 and −Ripple and never leaves that band.
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i <= 200000; i++)
        {
            double d = Db(p.At(i / 200000.0).S21);
            lo = Math.Min(lo, d); hi = Math.Max(hi, d);
        }
        Assert.Equal(-rippleDb, lo, 6);
        Assert.Equal(0.0, hi, 6);

        // Stopband: from its own edge outward, equiripple AT −Astop and never above it. The edge is
        // the selectivity factor ξ, which the degree equation determined from n and the two dB
        // figures — so finding it here is also a check that the degree equation was solved.
        double edge = double.NaN, peak = double.NegativeInfinity;
        for (int i = 0; i <= 400000; i++)
        {
            double w = 1.0 + i * 0.0005;
            double d = Db(p.At(w).S21);
            if (double.IsNaN(edge) && d <= -astopDb) edge = w;
            if (!double.IsNaN(edge)) peak = Math.Max(peak, d);
        }
        Assert.False(double.IsNaN(edge), "the response never reached the stated stopband floor");
        Assert.True(peak <= -astopDb + 1e-4,
            $"stopband rose to {peak:F9} dB, above the {-astopDb} dB floor");

        output.WriteLine($"Elliptic n = {n}, {rippleDb} dB / {astopDb} dB: " +
                         $"transition edge ξ = {edge:F5}, worst stopband {peak:F6} dB");
    }

    /// <summary>
    /// The point of the elliptic family, stated as a comparison rather than as a number: at the same
    /// order and the same two dB figures, it reaches the stopband floor sooner than every other
    /// family here. If that ever stops being true the family is not worth having.
    /// </summary>
    [Fact]
    public void Elliptic_ReachesTheFloorSoonerThanEveryOtherFamilyAtTheSameOrder()
    {
        const int N = 5;
        const double Ripple = 0.1, Astop = 60.0;

        double EdgeOf(FilterResponse r)
        {
            var p = FilterPrototype.Create(r, N, Ripple, Astop);
            for (int i = 0; i <= 2000000; i++)
            {
                double w = 1.0 + i * 0.0005;
                if (Db(p.At(w).S21) <= -Astop) return w;
            }
            return double.PositiveInfinity;
        }

        // Inverse Chebyshev is deliberately NOT in this comparison, and the reason is a finding
        // rather than an omission: its Ω = 1 is the STOPBAND edge, not the passband edge, so it
        // reaches the floor at exactly 1.0 by definition and the two families are not being asked
        // the same question. Comparing them fairly would mean rescaling one of them, and the
        // rescaling — not the response — would be what the test measured.
        double elliptic = EdgeOf(FilterResponse.Elliptic);
        foreach (var other in new[] { FilterResponse.Butterworth, FilterResponse.Chebyshev,
                                      FilterResponse.Bessel })
        {
            double e = EdgeOf(other);
            output.WriteLine($"{other}: reaches −{Astop} dB at ω = {e:F4}");
            Assert.True(elliptic < e, $"{other} reached the floor at {e:F4}, elliptic only at {elliptic:F4}");
        }
        output.WriteLine($"Elliptic: reaches −{Astop} dB at ω = {elliptic:F4}");
    }

    // ══ Rolloff ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The ultimate slope, for the families that HAVE one.
    /// </summary>
    /// <remarks>
    /// <b>brief-sys-6's gate says "every family reaches 20·n dB per decade far into the stopband",
    /// and that is true of three of the five.</b> Inverse Chebyshev and elliptic put their
    /// transmission zeros on the jω axis rather than at infinity, which is exactly what buys them
    /// their sharp transition, and the price is that the far stopband LEVELS OFF at the stated floor
    /// instead of falling away — at even order the ultimate slope is 0 dB/decade, and at odd order it
    /// is 20 dB/decade for every n, because only the one zero that went to infinity is left. That is
    /// the family working, not failing, so the two are gated on their floor (above) rather than on a
    /// slope they do not have.
    /// </remarks>
    [Theory]
    [InlineData(FilterResponse.Butterworth, 1)] [InlineData(FilterResponse.Butterworth, 4)]
    [InlineData(FilterResponse.Butterworth, 7)] [InlineData(FilterResponse.Chebyshev, 2)]
    [InlineData(FilterResponse.Chebyshev, 5)]   [InlineData(FilterResponse.Bessel, 3)]
    [InlineData(FilterResponse.Bessel, 6)]
    public void AnAllPoleFamilyRollsOffAt20nDbPerDecade(FilterResponse response, int n)
    {
        var p = FilterPrototype.Create(response, n, 0.1, 40.0);

        // Far enough out that the leading term dominates; one decade apart. The next term down is
        // O(1/ω), so a decade nearer in the answer is already wrong in the sixth place.
        double a = Db(p.At(1e5).S21), b = Db(p.At(1e6).S21);
        Assert.Equal(-20.0 * n, b - a, 6);
    }

    [Theory]
    [InlineData(FilterResponse.InvChebyshev, 4)] [InlineData(FilterResponse.Elliptic, 6)]
    public void AFamilyWithAxisZerosLevelsOffAtItsFloorInsteadOfRollingOff(FilterResponse response, int n)
    {
        var p = FilterPrototype.Create(response, n, 0.1, 40.0);
        Assert.Equal(-40.0, Db(p.At(1e6).S21), 6);
        Assert.Equal(-40.0, Db(p.At(1e7).S21), 6);
    }

    [Theory]
    [InlineData(FilterResponse.InvChebyshev, 5)] [InlineData(FilterResponse.Elliptic, 5)]
    public void AtOddOrderOneZeroIsAtInfinity_SoTheSlopeIs20DbPerDecadeRegardlessOfOrder(
        FilterResponse response, int n)
    {
        var p = FilterPrototype.Create(response, n, 0.1, 40.0);
        double a = Db(p.At(1e5).S21), b = Db(p.At(1e6).S21);
        Assert.Equal(-20.0, b - a, 5);
    }

    // ══ Unitarity ═════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>|S11|² + |S21|² = 1</c> at every frequency and every family, plus the two remaining rows
    /// of a lossless reciprocal two-port: <c>|S22| = |S11|</c> and the orthogonality of the two
    /// columns. The last one is the only gate that can catch a wrong <c>S22</c> PHASE, which is
    /// invisible to every magnitude comparison in this file.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFamilyAndOrder))]
    public void ThePrototypeIsLosslessAndReciprocal(FilterResponse response, int n)
    {
        var p = FilterPrototype.Create(response, n, 0.1, 40.0);

        for (double w = -30.0; w <= 30.0; w += 0.017)
        {
            var (s11, s21, s22) = p.At(w);

            // An explicit tolerance, not Assert.Equal's rounded comparison: two doubles that differ
            // in the last bit can still round to different values at 12 places when they straddle a
            // boundary, which is a property of the assertion rather than of the filter.
            double power = s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude;
            Assert.True(Math.Abs(power - 1.0) < 1e-12,
                $"{response} n = {n} at ω = {w:F3}: |S11|² + |S21|² = {power:G17}");
            Assert.True(Math.Abs(s11.Magnitude - s22.Magnitude) < 1e-12,
                $"{response} n = {n} at ω = {w:F3}: |S22| = {s22.Magnitude:G17} against |S11| = {s11.Magnitude:G17}");

            // Column orthogonality: S11·conj(S21) + S21·conj(S22) = 0.
            Complex ortho = s11 * Complex.Conjugate(s21) + s21 * Complex.Conjugate(s22);
            Assert.True(ortho.Magnitude < 1e-12,
                $"{response} n = {n} at ω = {w:F3}: columns are not orthogonal (|·| = {ortho.Magnitude:G6})");
        }
    }

    public static TheoryData<FilterResponse, int> EveryFamilyAndOrder()
    {
        var data = new TheoryData<FilterResponse, int>();
        foreach (var r in Enum.GetValues<FilterResponse>())
            for (int n = 1; n <= 7; n++)
                data.Add(r, n);
        return data;
    }

    // ══ The frequency transformations ═════════════════════════════════════════

    [Fact]
    public void AHighpassIsItsLowpassMirroredAboutTheCutoff()
    {
        const double Fc = 2.4e9;
        var lp = FilterNetwork.Create(FilterResponse.Chebyshev, NetworkForm.Lowpass,  4, Fc, 0, 0, 0.2, 40, 0);
        var hp = FilterNetwork.Create(FilterResponse.Chebyshev, NetworkForm.Highpass, 4, Fc, 0, 0, 0.2, 40, 0);

        // The map is ω ↦ ω_c²/ω, so a lowpass at 0.4·Fc is the same magnitude as a highpass at
        // 2.5·Fc. MAGNITUDE, not S itself: the two are different networks and their phases differ.
        foreach (double r in new[] { 0.05, 0.2, 0.4, 0.8, 1.0, 1.5, 3.0 })
        {
            double wLo = 2 * Math.PI * Fc * r, wHi = 2 * Math.PI * Fc / r;
            Assert.Equal(lp.At(wLo).S21.Magnitude, hp.At(wHi).S21.Magnitude, 12);
            Assert.Equal(lp.At(wLo).S11.Magnitude, hp.At(wHi).S11.Magnitude, 12);
        }
    }

    [Fact]
    public void ABandpassIsGeometricallySymmetricAboutTheGeometricMeanOfItsEdges()
    {
        const double F1 = 1.7e9, F2 = 2.3e9;
        double f0 = Math.Sqrt(F1 * F2);
        var bp = FilterNetwork.Create(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3, 0, F1, F2, 0.1, 40, 0);

        // Geometric, not arithmetic: f and f0²/f are the mirror pair. The arithmetic mirror
        // 2·f0 − f is NOT, and a filter drawn on a linear axis looks lopsided because of it.
        foreach (double f in new[] { 1.2e9, 1.6e9, 1.8e9, 2.0e9, 2.9e9, 6e9 })
        {
            double mirror = f0 * f0 / f;
            Assert.Equal(bp.At(2 * Math.PI * f).S21.Magnitude,
                         bp.At(2 * Math.PI * mirror).S21.Magnitude, 12);
        }

        // Band centre is that geometric mean, and both stated edges sit at the same level.
        Assert.Equal(bp.At(2 * Math.PI * F1).S21.Magnitude, bp.At(2 * Math.PI * F2).S21.Magnitude, 12);

        // The prototype variable is −1 at the lower edge, 0 at centre and +1 at the upper edge.
        Assert.Equal(-1.0, bp.PrototypeOmega(2 * Math.PI * F1), 12);
        Assert.Equal( 0.0, bp.PrototypeOmega(2 * Math.PI * f0), 12);
        Assert.Equal( 1.0, bp.PrototypeOmega(2 * Math.PI * F2), 12);
    }

    /// <summary>
    /// A bandpass at prototype order n is a network of degree 2n, and it is the DEGREE that shows in
    /// the skirt: the slope either side of the band is 20·(2n)·... per decade of the transformed
    /// variable, which on the real axis far above the band is 20·n dB per octave of ω. Gated here as
    /// the ratio between the prototype's own attenuation and the transformed one at the matching
    /// point, which is exact.
    /// </summary>
    [Fact]
    public void ABandpassAtOrderNIsANetworkOfDegree2N()
    {
        const double F1 = 0.9e9, F2 = 1.1e9;
        var proto = FilterPrototype.Create(FilterResponse.Butterworth, 3);
        var bp = FilterNetwork.Create(FilterResponse.Butterworth, NetworkForm.Bandpass, 3, 0, F1, F2, 0, 0, 0);

        // Two decades above the band, Ω ≈ ω/BW, so the bandpass attenuation is the prototype's at
        // that Ω — and the prototype is 6th order in ω because Ω itself is first order in ω only
        // once ω ≫ ω_0. One decade of ω is then one decade of Ω: 60 dB, not 120.
        double a = 20 * Math.Log10(bp.At(2 * Math.PI * 1e12).S21.Magnitude);
        double b = 20 * Math.Log10(bp.At(2 * Math.PI * 1e13).S21.Magnitude);
        Assert.Equal(-60.0, b - a, 4);

        // And the prototype the transformation is reading really is the 3rd-order one.
        Assert.Equal(3, proto.Order);
        Assert.Equal(4, proto.E.Length);
    }

    // ══ The limits ════════════════════════════════════════════════════════════

    /// <summary>
    /// The three degenerate DC cases. Each is an S-matrix with no Z form, no Y form, or neither —
    /// which is exactly why <c>IdealSBlockModel</c> stamps the wave constraint rather than a
    /// transformation of S, and why these are EXACT rather than nearly so.
    /// </summary>
    [Theory]
    [InlineData(FilterResponse.Butterworth,  3)]
    [InlineData(FilterResponse.Chebyshev,    3)]
    [InlineData(FilterResponse.Bessel,       4)]
    [InlineData(FilterResponse.InvChebyshev, 5)]
    [InlineData(FilterResponse.Elliptic,     5)]
    public void ALowpassAtDcIsAnExactThrough(FilterResponse response, int n)
    {
        var lp = FilterNetwork.Create(response, NetworkForm.Lowpass, n, 1e9, 0, 0, 0.1, 40, 0);
        var (s11, s21, s22) = lp.At(0.0);

        // S = [[0,1],[1,0]] — the ideal through, which has no Y matrix at all.
        Assert.Equal(0.0, s11.Magnitude, 12);
        Assert.Equal(0.0, s22.Magnitude, 12);
        Assert.Equal(1.0, s21.Real, 12);
        Assert.Equal(0.0, s21.Imaginary, 12);
    }

    /// <summary>
    /// The even-order Chebyshev and elliptic families are the exception, and it is theirs rather
    /// than ours: their characteristic function does not vanish at DC, so a lowpass built from one
    /// is mismatched at DC by exactly the stated ripple. Pinned here so it reads as the family's
    /// property rather than as a defect in the limit.
    /// </summary>
    [Theory]
    [InlineData(FilterResponse.Chebyshev, 4, 0.5)]
    [InlineData(FilterResponse.Elliptic,  6, 0.25)]
    public void AnEvenOrderEquirippleLowpassIsMismatchedAtDcByExactlyItsRipple(
        FilterResponse response, int n, double rippleDb)
    {
        var lp = FilterNetwork.Create(response, NetworkForm.Lowpass, n, 1e9, 0, 0, rippleDb, 60, 0);
        Assert.Equal(-rippleDb, Db(lp.At(0.0).S21), 9);
    }

    /// <summary>
    /// A form that blocks DC is an exact OPEN at port 1 — for every family whose transmission zeros
    /// are all at infinity, and for the two whose are not at ODD order, where the last zero has gone
    /// to infinity anyway.
    /// </summary>
    [Theory]
    [InlineData(NetworkForm.Highpass)]
    [InlineData(NetworkForm.Bandpass)]
    public void AFormThatBlocksDcIsAnExactOpenAtPortOne(NetworkForm form)
    {
        foreach (var response in Enum.GetValues<FilterResponse>())
        for (int n = 1; n <= 7; n++)
        {
            if (HasFiniteZeros(response) && n % 2 == 0) continue;   // the next test's business

            var net = FilterNetwork.Create(response, form, n, 1e9, 0.9e9, 1.1e9, 0.1, 40, 0);
            var (s11, s21, s22) = net.At(0.0);

            Assert.Equal(0.0, s21.Magnitude, 12);
            Assert.Equal(1.0, s11.Magnitude, 12);
            Assert.Equal(1.0, s22.Magnitude, 12);

            // Port 1 is an OPEN — S11 = +1 exactly, not −1. The sign is a choice of which of two
            // dual networks the response is realised as, and it is pinned so the answer is the
            // series-first ladder a reader pictures. Port 2's sign then follows from the parity of
            // the network and is NOT free: at even degree it is a short, which is what a shunt
            // element at that end of a series-first ladder is. Both are stampable and neither has a
            // Z form, a Y form, or in the mixed case either — which is the whole argument for the
            // wave constraint.
            Assert.Equal(1.0, s11.Real, 12);
            Assert.True(Math.Abs(Math.Abs(s22.Real) - 1.0) < 1e-12, $"{response} n = {n}: S22 = {s22}");
        }
    }

    /// <summary>
    /// The exception, and it is the family's property rather than a defect in the limit.
    /// </summary>
    /// <remarks>
    /// <b>brief-sys-6's limits gate says a highpass at ω = 0 is an exact open, and at EVEN order the
    /// two axis-zero families are not.</b> An even-order inverse Chebyshev or elliptic response
    /// levels off at its stated stopband floor rather than falling away — that is what putting the
    /// transmission zeros on the jω axis buys and costs — so its highpass at DC is not an open but a
    /// −Astop pad, and its bandpass at DC likewise. The block is still exactly lossless there, which
    /// is what this asserts: the energy that does not reflect is transmitted, to the last bit.
    /// </remarks>
    [Theory]
    [InlineData(FilterResponse.InvChebyshev, NetworkForm.Highpass)]
    [InlineData(FilterResponse.InvChebyshev, NetworkForm.Bandpass)]
    [InlineData(FilterResponse.Elliptic,     NetworkForm.Highpass)]
    [InlineData(FilterResponse.Elliptic,     NetworkForm.Bandpass)]
    public void AnEvenOrderAxisZeroFamilySitsAtItsFloorAtDcRatherThanBeingAnOpen(
        FilterResponse response, NetworkForm form)
    {
        foreach (int n in (int[])[2, 4, 6])
        {
            var net = FilterNetwork.Create(response, form, n, 1e9, 0.9e9, 1.1e9, 0.1, 40, 0);
            var (s11, s21, s22) = net.At(0.0);

            Assert.Equal(-40.0, Db(s21), 9);
            double power = s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude;
            Assert.True(Math.Abs(power - 1.0) < 1e-12,
                $"{response} n = {n} {form}: |S11|² + |S21|² = {power:G17} at DC");
            Assert.True(Math.Abs(s11.Magnitude - s22.Magnitude) < 1e-12);
        }
    }

    private static bool HasFiniteZeros(FilterResponse r)
        => r is FilterResponse.InvChebyshev or FilterResponse.Elliptic;

    // ══ Insertion loss ════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0.0)] [InlineData(0.5)] [InlineData(3.0)] [InlineData(20.0)]
    public void InsertionLossMultipliesS21AndLeavesS11Alone(double ilDb)
    {
        var ideal = FilterNetwork.Create(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3, 0, 0.9e9, 1.1e9, 0.1, 40, 0.0);
        var lossy = FilterNetwork.Create(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3, 0, 0.9e9, 1.1e9, 0.1, 40, ilDb);
        double amp = Math.Pow(10.0, -ilDb / 20.0);

        for (double f = 0.5e9; f <= 2e9; f += 0.037e9)
        {
            double w = 2 * Math.PI * f;
            Assert.Equal(ideal.At(w).S21 * amp, lossy.At(w).S21);

            // S11 is untouched, so the block genuinely DISSIPATES rather than reflecting what it
            // loses — the loss shows as |S11|² + |S21|² < 1, which is what a real filter's
            // dissipation does.
            Assert.Equal(ideal.At(w).S11, lossy.At(w).S11);
            Assert.Equal(ideal.At(w).S22, lossy.At(w).S22);
        }

        var (s11, s21, _) = lossy.At(2 * Math.PI * 1e9);
        double power = s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude;
        Assert.True(ilDb == 0.0 ? Math.Abs(power - 1.0) < 1e-12 : power < 1.0 - 1e-9);
    }

    // ══ Refusals ══════════════════════════════════════════════════════════════

    [Fact]
    public void AnOrderBelowOneIsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(
               () => FilterPrototype.Create(FilterResponse.Butterworth, 0));

    [Theory]
    [InlineData(0.0)] [InlineData(-1.0)] [InlineData(double.NaN)]
    public void ARippleThatIsNotAPositiveNumberOfDbIsRefused(double rippleDb)
        => Assert.Throws<ArgumentOutOfRangeException>(
               () => FilterPrototype.Create(FilterResponse.Chebyshev, 3, rippleDb));

    [Fact]
    public void AnEllipticStopbandNoDeeperThanItsPassbandRippleIsRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => FilterPrototype.Create(FilterResponse.Elliptic, 4, rippleDb: 1.0, astopDb: 0.5));
        Assert.Contains("deeper than its passband ripple", ex.Message);
    }

    [Theory]
    [InlineData(NetworkForm.Lowpass,  0.0)]
    [InlineData(NetworkForm.Highpass, -1e9)]
    public void ACutoffThatIsNotPositiveIsRefused(NetworkForm form, double fcHz)
        => Assert.Throws<ArgumentOutOfRangeException>(
               () => FilterNetwork.Create(FilterResponse.Butterworth, form, 3, fcHz, 0, 0, 0.1, 40, 0));

    [Theory]
    [InlineData(1.1e9, 0.9e9)]
    [InlineData(1e9,   1e9)]
    [InlineData(0.0,   1e9)]
    public void BandEdgesThatAreNotAnAscendingPositivePairAreRefused(double f1, double f2)
        => Assert.Throws<ArgumentOutOfRangeException>(
               () => FilterNetwork.Create(FilterResponse.Butterworth, NetworkForm.Bandpass, 3, 0, f1, f2, 0.1, 40, 0));

    // ══ A parameter the family does not read ══════════════════════════════════

    /// <summary>
    /// Ignored, never refused. A user switching Chebyshev to Butterworth must not have to clear a
    /// ripple field first, and a user switching bandpass to lowpass must not have to clear the band
    /// edges — including edges that would be REFUSED if the form read them.
    /// </summary>
    [Fact]
    public void AParameterTheSelectedFamilyOrFormDoesNotReadIsIgnored()
    {
        var a = FilterPrototype.Create(FilterResponse.Butterworth, 4, rippleDb: 3.0, astopDb: 12.0);
        var b = FilterPrototype.Create(FilterResponse.Butterworth, 4, rippleDb: 0.1, astopDb: 90.0);
        Assert.Equal(a.E, b.E);
        Assert.Equal(a.Alpha, b.Alpha);

        // Band edges that are not even a valid band, on a form that does not read them.
        var lp = FilterNetwork.Create(FilterResponse.Butterworth, NetworkForm.Lowpass, 3,
                                      1e9, f1Hz: 5e9, f2Hz: 1e9, 0.1, 40, 0);
        Assert.Equal(-10.0 * Math.Log10(2.0), Db(lp.At(2 * Math.PI * 1e9).S21), 9);

        // …and a cutoff that is not a cutoff, on a form that does not read it.
        var bp = FilterNetwork.Create(FilterResponse.Butterworth, NetworkForm.Bandpass, 3,
                                      fcHz: -1.0, f1Hz: 0.9e9, f2Hz: 1.1e9, 0.1, 40, 0);
        Assert.Equal(0.0, bp.PrototypeOmega(2 * Math.PI * Math.Sqrt(0.9e9 * 1.1e9)), 12);
    }
}
