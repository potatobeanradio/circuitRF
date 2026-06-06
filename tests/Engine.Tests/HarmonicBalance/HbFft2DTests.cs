using System.Numerics;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Unit tests for HbFft2D — the 2-D separable FFT layer for two-tone HB.
///
/// Each test group pins one invariant so a future regression points straight at the broken bit:
///   Group A: GridSizes (sizing formula)
///   Group B: Forward2D amplitude convention (DC bin, sinusoids per axis, cross-product)
///   Group C: SpecGet conjugate symmetry and periodic wrap
///   Group D: ConversionWeight2D (per-axis and DC-row factors)
///   Group E: Inverse2D round-trip (Inverse then Forward recovers V_HB)
///   Group F: MixingOmega (just the formula)
/// </summary>
public class HbFft2DTests
{
    private const double Tol = 1e-9;

    // ═══════════════════════════════════════════════════════════════
    // Group A — GridSizes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GridSizes_Order5_Oversample1_ReturnsMinimumPow2()
    {
        // order=5 → minN = 4×5 = 20 → nextpow2(20) = 32
        var (N1, N2) = HbFft2D.GridSizes(5, 5, 1);
        Assert.Equal(32, N1);
        Assert.Equal(32, N2);
    }

    [Fact]
    public void GridSizes_Order1_Oversample1_Minimum4()
    {
        // order=1 → minN = max(4, 4×1) = 4 → nextpow2(4) = 4
        var (N1, N2) = HbFft2D.GridSizes(1, 1, 1);
        Assert.Equal(4, N1);
        Assert.Equal(4, N2);
    }

    [Fact]
    public void GridSizes_UnequalOrders_IndependentAxes()
    {
        // axis1: order=3 → minN=12 → nextpow2(12)=16
        // axis2: order=7 → minN=28 → nextpow2(28)=32
        var (N1, N2) = HbFft2D.GridSizes(3, 7, 1);
        Assert.Equal(16, N1);
        Assert.Equal(32, N2);
    }

    [Fact]
    public void GridSizes_Oversample2_DoublesEachAxis()
    {
        var (N1b, N2b) = HbFft2D.GridSizes(5, 5, 1);
        var (N1,  N2 ) = HbFft2D.GridSizes(5, 5, 2);
        Assert.Equal(N1b * 2, N1);
        Assert.Equal(N2b * 2, N2);
    }

    // ═══════════════════════════════════════════════════════════════
    // Group B — Forward2D amplitude convention
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Forward2D_ConstantOne_OnlyDcNonzero()
    {
        // x[i1,i2] = 1 everywhere → only spec[0,0] should be non-zero = 1.
        const int N1 = 8, N2 = 8;
        var x = new double[N1, N2];
        for (int i1 = 0; i1 < N1; i1++)
            for (int i2 = 0; i2 < N2; i2++)
                x[i1, i2] = 1.0;

        HbFft2D.Forward2D(x, out var spec);

        Assert.Equal(N1, spec.GetLength(0));
        Assert.Equal(N2 / 2 + 1, spec.GetLength(1));

        // DC bin = 1.0
        Assert.Equal(1.0, spec[0, 0].Real, Tol);
        Assert.Equal(0.0, spec[0, 0].Imaginary, Tol);

        // All other bins zero
        for (int k1 = 0; k1 < N1; k1++)
            for (int k2 = 0; k2 <= N2 / 2; k2++)
            {
                if (k1 == 0 && k2 == 0) continue;
                Assert.True(spec[k1, k2].Magnitude < Tol,
                    $"Expected zero at ({k1},{k2}), got {spec[k1, k2]}");
            }
    }

    [Fact]
    public void Forward2D_CosAlongAxis2_PhasorsUnity()
    {
        // x[i1,i2] = cos(2π·1·i2/N2) for all i1 — pure axis-2 harmonic k2=1.
        // Expected: spec[0,1] = 1.0 (full-amplitude convention, k1=0 → divide by N1,
        //           axis-2 AC bin at k2=1 → divide by N2/2, raw = N1*(N2/2)).
        const int N1 = 8, N2 = 8;
        var x = new double[N1, N2];
        for (int i1 = 0; i1 < N1; i1++)
            for (int i2 = 0; i2 < N2; i2++)
                x[i1, i2] = Math.Cos(2 * Math.PI * i2 / N2);

        HbFft2D.Forward2D(x, out var spec);

        // Carrier at (0, 1) should be 1.0
        Assert.Equal(1.0, spec[0, 1].Real,      1e-10);
        Assert.Equal(0.0, spec[0, 1].Imaginary, 1e-10);

        // No energy at other bins (including (k1≠0, 1))
        for (int k1 = 0; k1 < N1; k1++)
            for (int k2 = 0; k2 <= N2 / 2; k2++)
            {
                if (k1 == 0 && k2 == 1) continue;
                Assert.True(spec[k1, k2].Magnitude < 1e-10,
                    $"Unexpected energy at ({k1},{k2}): {spec[k1, k2]}");
            }
    }

    [Fact]
    public void Forward2D_CosAlongAxis1_PhasorsUnity()
    {
        // x[i1,i2] = cos(2π·1·i1/N1) for all i2 — pure axis-1 harmonic k1=1.
        // Raw DFT: each row (axis-2 FFT) gives rawRow[0] = N2*cos(2π*i1/N1) (DC row = no axis-2 variation).
        // Then axis-1 FFT for k2=0: rawCol[1] = Σ_i1 N2*cos(2π*i1/N1)*e^{-j2π*i1/N1} = N1*N2/2.
        // After convention: spec[1,0] = (N1*N2/2) / (N1/2 * N2) = 1.0.
        const int N1 = 8, N2 = 8;
        var x = new double[N1, N2];
        for (int i1 = 0; i1 < N1; i1++)
            for (int i2 = 0; i2 < N2; i2++)
                x[i1, i2] = Math.Cos(2 * Math.PI * i1 / N1);

        HbFft2D.Forward2D(x, out var spec);

        Assert.Equal(1.0, spec[1, 0].Real,      1e-10);
        Assert.Equal(0.0, spec[1, 0].Imaginary, 1e-10);

        for (int k1 = 0; k1 < N1; k1++)
            for (int k2 = 0; k2 <= N2 / 2; k2++)
            {
                if (k1 == 1 && k2 == 0) continue;
                Assert.True(spec[k1, k2].Magnitude < 1e-10,
                    $"Unexpected energy at ({k1},{k2}): {spec[k1, k2]}");
            }
    }

    [Fact]
    public void Forward2D_CrossProductCos_PhasorUnity()
    {
        // x[i1,i2] = cos(2π·i1/N1)·cos(2π·i2/N2)
        // = ¼·[e^{j2π(i1/N1 + i2/N2)} + e^{j2π(i1/N1 - i2/N2)} + conj terms]
        // In our full-amplitude convention:
        //   v = Re{V_HB[1,1]·e^{j(φ1+φ2)}} + Re{V_HB[1,-1]·e^{j(φ1-φ2)}}
        // So V_HB[1,1] = 0.5 and V_HB[1,-1] = 0.5.
        // V_HB[1,-1] = conj(V_HB[-1,1]) = conj(spec[(N1-1)%N1, 1]) in stored half.
        // But SpecGet(spec,1,-1) = conj(SpecGet(spec,-1,1)) → we test via SpecGet.
        const int N1 = 8, N2 = 8;
        var x = new double[N1, N2];
        for (int i1 = 0; i1 < N1; i1++)
            for (int i2 = 0; i2 < N2; i2++)
                x[i1, i2] = Math.Cos(2 * Math.PI * i1 / N1) * Math.Cos(2 * Math.PI * i2 / N2);

        HbFft2D.Forward2D(x, out var spec);

        // Carrier at (1,1) should be 0.5
        var v11 = HbFft2D.SpecGet(spec, 1, 1);
        Assert.Equal(0.5, v11.Real,      1e-10);
        Assert.Equal(0.0, v11.Imaginary, 1e-10);

        // (1,-1) via conjugate symmetry: SpecGet(spec,1,-1) = conj(SpecGet(spec,-1,1))
        var v1m1 = HbFft2D.SpecGet(spec, 1, -1);
        Assert.Equal(0.5, v1m1.Real,      1e-10);
        Assert.Equal(0.0, v1m1.Imaginary, 1e-10);
    }

    [Fact]
    public void Forward2D_DcPlusTwoSinusoids_CorrectAmplitudes()
    {
        // x = 2.0 + cos(2π·i1/N1) + 0.5·cos(2π·i2/N2)
        // V_HB[0,0]=2.0, V_HB[1,0]=1.0, V_HB[0,1]=0.5
        const int N1 = 8, N2 = 8;
        var x = new double[N1, N2];
        for (int i1 = 0; i1 < N1; i1++)
            for (int i2 = 0; i2 < N2; i2++)
                x[i1, i2] = 2.0
                    + Math.Cos(2 * Math.PI * i1 / N1)
                    + 0.5 * Math.Cos(2 * Math.PI * i2 / N2);

        HbFft2D.Forward2D(x, out var spec);

        Assert.Equal(2.0, spec[0, 0].Real,      1e-10);
        Assert.Equal(1.0, spec[1, 0].Real,      1e-10);
        Assert.Equal(0.0, spec[1, 0].Imaginary, 1e-10);
        Assert.Equal(0.5, spec[0, 1].Real,      1e-10);
        Assert.Equal(0.0, spec[0, 1].Imaginary, 1e-10);
    }

    // ═══════════════════════════════════════════════════════════════
    // Group C — SpecGet conjugate symmetry and periodic wrap
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SpecGet_PositiveK2_DirectLookup()
    {
        // Build a spectrum with a known value at (1,2).
        const int N1 = 8, N2 = 8;
        var spec = new Complex[N1, N2 / 2 + 1];
        spec[1, 2] = new Complex(3.0, 4.0);

        var got = HbFft2D.SpecGet(spec, 1, 2);
        Assert.Equal(3.0, got.Real);
        Assert.Equal(4.0, got.Imaginary);
    }

    [Fact]
    public void SpecGet_NegativeK2_ConjugateSymmetry()
    {
        // SpecGet(spec, dk1, -k2) = conj(SpecGet(spec, -dk1, k2))
        // Set spec[-1 mod N1, 2] = spec[N1-1, 2] = (3,4)
        // Then SpecGet(spec, 1, -2) = conj(SpecGet(spec, -1, 2)) = conj(spec[N1-1, 2]) = (3,-4)
        const int N1 = 8, N2 = 8;
        var spec = new Complex[N1, N2 / 2 + 1];
        spec[N1 - 1, 2] = new Complex(3.0, 4.0);   // this is the k1=-1 bin

        var got = HbFft2D.SpecGet(spec, 1, -2);     // = conj(SpecGet(spec,-1,2)) = conj(3+4j) = (3,-4)
        Assert.Equal(3.0, got.Real,       1e-14);
        Assert.Equal(-4.0, got.Imaginary, 1e-14);
    }

    [Fact]
    public void SpecGet_PeriodicWrapAxis1()
    {
        // spec[1, 0] = (5,6). Then SpecGet(spec, 1-N1, 0) should return the same (periodic).
        const int N1 = 8, N2 = 8;
        var spec = new Complex[N1, N2 / 2 + 1];
        spec[1, 0] = new Complex(5.0, 6.0);

        var got = HbFft2D.SpecGet(spec, 1 - N1, 0);   // dk1 = 1 - 8 = -7 → wraps to 1
        Assert.Equal(5.0, got.Real);
        Assert.Equal(6.0, got.Imaginary);
    }

    [Fact]
    public void SpecGet_OutOfRangeK2_ReturnsZero()
    {
        const int N1 = 8, N2 = 8;
        var spec = new Complex[N1, N2 / 2 + 1];

        var got = HbFft2D.SpecGet(spec, 0, N2 / 2 + 1);   // k2 > N2/2 → zero
        Assert.Equal(Complex.Zero, got);
    }

    [Fact]
    public void SpecGet_NegativeK1_PeriodicWrap()
    {
        // spec[N1-2, 1] = (7,0). SpecGet(spec, -2, 1) → k1w = ((-2)+8)%8 = 6 = N1-2.
        const int N1 = 8, N2 = 8;
        var spec = new Complex[N1, N2 / 2 + 1];
        spec[N1 - 2, 1] = new Complex(7.0, 0.0);

        var got = HbFft2D.SpecGet(spec, -2, 1);
        Assert.Equal(7.0, got.Real,  1e-14);
        Assert.Equal(0.0, got.Imaginary, 1e-14);
    }

    // ═══════════════════════════════════════════════════════════════
    // Group D — ConversionWeight2D
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConversionWeight2D_AcRow_DcBin_Both()
    {
        // AC row (1,0), DC bin (0,0): f1=1, f2=1, fRow=1 → W=1.0
        double w = HbFft2D.ConversionWeight2D(1, 0, 0, 0);
        Assert.Equal(1.0, w, 1e-15);
    }

    [Fact]
    public void ConversionWeight2D_AcRow_AcBin_Axis1Only()
    {
        // AC row (1,0), AC bin (1,0): f1=0.5, f2=1, fRow=1 → W=0.5
        double w = HbFft2D.ConversionWeight2D(1, 0, 1, 0);
        Assert.Equal(0.5, w, 1e-15);
    }

    [Fact]
    public void ConversionWeight2D_AcRow_AcBin_Axis2Only()
    {
        // AC row (0,1), AC bin (0,1): f1=1, f2=0.5, fRow=1 → W=0.5
        double w = HbFft2D.ConversionWeight2D(0, 1, 0, 1);
        Assert.Equal(0.5, w, 1e-15);
    }

    [Fact]
    public void ConversionWeight2D_AcRow_AcBin_BothAxes()
    {
        // AC row (1,1), AC bin (1,2): bin is non-DC → fBin=0.5 (GLOBAL, not ½·½), fRow=1 → W=0.5.
        // The FD-Jacobian oracle requires the global ½ here, not a per-axis ¼ (cross-bin double-halving).
        double w = HbFft2D.ConversionWeight2D(1, 1, 1, 2);
        Assert.Equal(0.5, w, 1e-15);
    }

    [Fact]
    public void ConversionWeight2D_DcRow_DcBin()
    {
        // DC row (0,0), DC bin (0,0): f1=1, f2=1, fRow=0.5 → W=0.5
        double w = HbFft2D.ConversionWeight2D(0, 0, 0, 0);
        Assert.Equal(0.5, w, 1e-15);
    }

    [Fact]
    public void ConversionWeight2D_DcRow_AcBin_Axis1()
    {
        // DC row (0,0), AC bin (1,0): f1=0.5, f2=1, fRow=0.5 → W=0.25
        double w = HbFft2D.ConversionWeight2D(0, 0, 1, 0);
        Assert.Equal(0.25, w, 1e-15);
    }

    [Fact]
    public void ConversionWeight2D_DcRow_AcBin_BothAxes()
    {
        // DC row (0,0), AC bin (1,1): bin non-DC → fBin=0.5 (GLOBAL), fRow=0.5 → W=0.25.
        double w = HbFft2D.ConversionWeight2D(0, 0, 1, 1);
        Assert.Equal(0.25, w, 1e-15);
    }

    [Fact]
    public void ConversionWeight2D_ReducesToSingleTone_WhenAxis2Zero()
    {
        // With k2_row=0 and dk2=0, should match single-tone ConversionWeight.
        // Single-tone: CW(k=0, j=0)=0.5; CW(k=1, j=0)=1.0; CW(k=1, j=1)=0.5; CW(k=0, j=1)=0.25
        Assert.Equal(0.5,  HbFft2D.ConversionWeight2D(0, 0, 0, 0), 1e-15);  // CW(k=0, j=0)
        Assert.Equal(1.0,  HbFft2D.ConversionWeight2D(1, 0, 0, 0), 1e-15);  // CW(k=1, j=0)
        Assert.Equal(0.5,  HbFft2D.ConversionWeight2D(1, 0, 1, 0), 1e-15);  // CW(k=1, j=1)
        Assert.Equal(0.25, HbFft2D.ConversionWeight2D(0, 0, 1, 0), 1e-15);  // CW(k=0, j=1)
    }

    // ═══════════════════════════════════════════════════════════════
    // Group E — Inverse2D round-trip
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Inverse2D_ThenForward_RecoversVhb()
    {
        // 1. Assign known V_HB at each retained mixing index.
        // 2. Inverse2D → time-domain x.
        // 3. Forward2D → spectrum.
        // 4. Extract spectrum at retained indices — must recover V_HB.
        const int maxMixOrder = 3;
        var grid = new MixingGrid(maxMixOrder);
        int M    = grid.MixCount;

        var (N1, N2) = HbFft2D.GridSizes(maxMixOrder, maxMixOrder, 2);

        // Assign non-trivial V_HB: use index as real part, 0.1×index as imaginary.
        var vIn = new Complex[M];
        vIn[0] = new Complex(0.5, 0.0);   // DC must be real
        for (int m = 1; m < M; m++)
            vIn[m] = new Complex(0.1 * m, 0.05 * m);

        // Inverse
        var x = new double[N1, N2];
        HbFft2D.Inverse2D(grid, vIn, N1, N2, x);

        // Forward
        HbFft2D.Forward2D(x, out var spec);

        // Check each retained (k1,k2): spec lookup must match vIn[m]
        for (int m = 0; m < M; m++)
        {
            var (k1, k2) = grid.ToneOf(m);
            var got = HbFft2D.SpecGet(spec, k1, k2);
            Assert.True(Complex.Abs(got - vIn[m]) < 1e-9,
                $"mixIdx={m} (k1={k1},k2={k2}): expected {vIn[m]}, got {got}, err={Complex.Abs(got-vIn[m]):e3}");
        }
    }

    [Fact]
    public void Inverse2D_DcOnlySignal_ConstantOutput()
    {
        // V_HB[0] = 3.0 (DC only) → x should be constant 3.0
        const int maxMixOrder = 2;
        var grid = new MixingGrid(maxMixOrder);
        var (N1, N2) = HbFft2D.GridSizes(maxMixOrder, maxMixOrder, 1);

        var vIn = new Complex[grid.MixCount];
        vIn[0] = new Complex(3.0, 0.0);

        var x = new double[N1, N2];
        HbFft2D.Inverse2D(grid, vIn, N1, N2, x);

        for (int i1 = 0; i1 < N1; i1++)
            for (int i2 = 0; i2 < N2; i2++)
                Assert.Equal(3.0, x[i1, i2], 1e-10);
    }

    [Fact]
    public void Inverse2D_SingleCarrier_ProducesCosine()
    {
        // V_HB[k1=1, k2=0] = 1.0 → x[i1,i2] = cos(2π·i1/N1)
        const int maxMixOrder = 2;
        var grid = new MixingGrid(maxMixOrder);
        var (N1, N2) = HbFft2D.GridSizes(maxMixOrder, maxMixOrder, 1);

        var vIn = new Complex[grid.MixCount];
        // Find the index of (1,0)
        int m10 = grid.IndexOf(1, 0);
        Assert.True(m10 >= 0, "grid must contain (1,0)");
        vIn[m10] = new Complex(1.0, 0.0);

        var x = new double[N1, N2];
        HbFft2D.Inverse2D(grid, vIn, N1, N2, x);

        for (int i1 = 0; i1 < N1; i1++)
        {
            double expected = Math.Cos(2 * Math.PI * i1 / N1);
            for (int i2 = 0; i2 < N2; i2++)
                Assert.Equal(expected, x[i1, i2], 1e-10);
        }
    }

    [Fact]
    public void Inverse2D_SingleCarrierAxis2_ProducesCosine()
    {
        // V_HB[k1=0, k2=1] = 1.0 → x[i1,i2] = cos(2π·i2/N2)
        const int maxMixOrder = 2;
        var grid = new MixingGrid(maxMixOrder);
        var (N1, N2) = HbFft2D.GridSizes(maxMixOrder, maxMixOrder, 1);

        var vIn = new Complex[grid.MixCount];
        int m01 = grid.IndexOf(0, 1);
        Assert.True(m01 >= 0);
        vIn[m01] = new Complex(1.0, 0.0);

        var x = new double[N1, N2];
        HbFft2D.Inverse2D(grid, vIn, N1, N2, x);

        for (int i2 = 0; i2 < N2; i2++)
        {
            double expected = Math.Cos(2 * Math.PI * i2 / N2);
            for (int i1 = 0; i1 < N1; i1++)
                Assert.Equal(expected, x[i1, i2], 1e-10);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Group F — MixingOmega
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MixingOmega_Carrier1_ReturnsOmega1()
    {
        double ω1 = 2 * Math.PI * 1.995e9;
        double ω2 = 2 * Math.PI * 2.005e9;
        Assert.Equal(ω1, HbFft2D.MixingOmega(1, 0, ω1, ω2), 1.0);
    }

    [Fact]
    public void MixingOmega_Carrier2_ReturnsOmega2()
    {
        double ω1 = 2 * Math.PI * 1.995e9;
        double ω2 = 2 * Math.PI * 2.005e9;
        Assert.Equal(ω2, HbFft2D.MixingOmega(0, 1, ω1, ω2), 1.0);
    }

    [Fact]
    public void MixingOmega_Im3Lower_Returns2Omega1MinusOmega2()
    {
        double ω1 = 2 * Math.PI * 1.995e9;
        double ω2 = 2 * Math.PI * 2.005e9;
        double expected = 2 * ω1 - ω2;
        Assert.Equal(expected, HbFft2D.MixingOmega(2, -1, ω1, ω2), 1.0);
    }
}
