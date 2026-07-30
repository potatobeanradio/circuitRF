// ================================================================
//  InterpolationTests.cs  —  Interpolation contract tests
//
//  Critical invariant: interpolating at the stored frequency points
//  must return those exact values (the spline is interpolating, not
//  approximating).
//
//  Analytical test: ideal delay line whose phase is linear in
//  frequency — cubic spline of the real and imaginary parts must
//  recover the mid-point value within tolerance.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NumFlat;
using RfCore;
using Xunit;

namespace RfCore.Tests;

public class InterpolationTests
{
    private static readonly string TestDataDir =
        Path.Combine(AppContext.BaseDirectory, "testdata");

    // ================================================================
    //  Helper: ideal delay-line SNP
    //
    //  2-port, S21 = exp(-j·2π·f·τ), S12 = S21,
    //  S11 = S22 = 0  (matched, lossless delay)
    // ================================================================

    private static SNP MakeDelayLine(double[] freqs, double tauSeconds)
    {
        var mats = new Mat<Complex>[freqs.Length];
        for (int fi = 0; fi < freqs.Length; fi++)
        {
            double phase = -2.0 * Math.PI * freqs[fi] * tauSeconds;
            var s21 = Complex.FromPolarCoordinates(1.0, phase);
            var m   = new Mat<Complex>(2, 2);
            m[0, 1] = s21;
            m[1, 0] = s21;
            // S11 = S22 = 0 (default)
            mats[fi] = m;
        }
        return new SNP(freqs, mats, MatrixType.S, MatrixFormat.RI);
    }

    // ================================================================
    //  At-own-points: exact recovery
    //
    //  Interpolating at the stored frequency points must return the
    //  original values to machine precision (the spline passes through
    //  the data — it is interpolating, not approximating).
    // ================================================================

    [Theory]
    [InlineData("2SC5226A.s2p")]
    [InlineData("potentially_unstable_amp.s2p")]
    [InlineData("Test_5Port.s5p")]
    public void AtOwnPoints_SplineExact(string filename)
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, filename));
        var interp = RFNetwork.Interpolate(snp, snp.Frequencies);

        double rms = RFNetwork.CompareRMSValue(snp, interp);
        Assert.True(rms < 1e-10,
            $"{filename} at-own-points RMS={rms:G4}, expected < 1e-10");
    }

    [Theory]
    [InlineData("2SC5226A.s2p")]
    [InlineData("potentially_unstable_amp.s2p")]
    public void AtOwnPoints_LinearExact(string filename)
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, filename));
        var interp = RFNetwork.Interpolate(snp, snp.Frequencies,
                                           method: InterpolationMethod.Linear);

        double rms = RFNetwork.CompareRMSValue(snp, interp);
        Assert.True(rms < 1e-10,
            $"{filename} at-own-points linear RMS={rms:G4}, expected < 1e-10");
    }

    // ================================================================
    //  Analytical: ideal delay line
    //
    //  Cubic spline should interpolate S21 = exp(-j·2π·f·τ) accurately
    //  between stored frequency points when the frequency grid is fine
    //  enough that sin/cos don't oscillate more than a half-cycle per
    //  interval.
    // ================================================================

    [Fact]
    public void DelayLine_SplineMidpoint_AccurateToTolerance()
    {
        // 6 points, 0–0.5 GHz in 0.1 GHz steps, τ = 0.1 ns (100 ps).
        // Phase per interval = 2π·0.1e9·0.1e-9 = 0.063 rad — small, spline is accurate.
        // Error bound: O(h^4), h = 0.063 rad → ≈ 1e-6.
        double tau  = 0.1e-9;
        double[] xs = { 0, 0.1e9, 0.2e9, 0.3e9, 0.4e9, 0.5e9 };
        var snp = MakeDelayLine(xs, tau);

        // Interpolate at 0.25 GHz — interior midpoint of [0.2, 0.3 GHz].
        // Natural cubic splines have larger endpoint errors (cc[0]=0 boundary condition
        // doesn't match the actual curvature); interior midpoints are O(h^4).
        double fMid = 0.25e9;
        var interp = RFNetwork.Interpolate(snp, new[] { fMid });

        double phaseExact = -2.0 * Math.PI * fMid * tau;
        Complex s21Exact = Complex.FromPolarCoordinates(1.0, phaseExact);
        Complex s21Got   = interp.Matrices[0][1, 0];

        double errReal = Math.Abs(s21Got.Real      - s21Exact.Real);
        double errImag = Math.Abs(s21Got.Imaginary - s21Exact.Imaginary);

        // 1e-4 is conservative — the natural spline on a 6-point grid gives ~1e-5 here,
        // well below the 1e-6 Hero 1 acceptance spec when a denser grid is used.
        Assert.True(errReal < 1e-4,
            $"S21.Real error={errReal:G4} at interior midpoint, expected < 1e-4");
        Assert.True(errImag < 1e-4,
            $"S21.Imag error={errImag:G4} at interior midpoint, expected < 1e-4");
    }

    [Fact]
    public void DelayLine_LinearInterp_MidpointCloser_WithDenseGrid()
    {
        // Dense grid (0.01 GHz spacing), τ = 0.1 ns.
        // Phase per interval = 2π·0.01e9·0.1e-9 = 0.006 rad.
        // Linear interpolation error: O(h^2) → ≈ 5e-6.
        double tau = 0.1e-9;
        var xs = new double[21]; // 0..0.2 GHz in 0.01 GHz steps
        for (int i = 0; i < 21; i++) xs[i] = i * 0.01e9;
        var snp = MakeDelayLine(xs, tau);

        double fMid = 0.005e9;
        var interp = RFNetwork.Interpolate(snp, new[] { fMid },
                                           method: InterpolationMethod.Linear);

        double phaseExact = -2.0 * Math.PI * fMid * tau;
        Complex s21Exact = Complex.FromPolarCoordinates(1.0, phaseExact);
        Complex s21Got   = interp.Matrices[0][1, 0];

        double err = (s21Got - s21Exact).Magnitude;
        Assert.True(err < 1e-4, $"Linear midpoint error={err:G4}, expected < 1e-4");
    }

    // ================================================================
    //  Return type: result has the requested interpolateIn type
    // ================================================================

    [Fact]
    public void Result_HasRequestedType_S()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        var r = RFNetwork.Interpolate(snp, snp.Frequencies, interpolateIn: MatrixType.S);
        Assert.Equal(MatrixType.S, r.Type);
    }

    [Fact]
    public void Result_HasRequestedType_Z()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        var r = RFNetwork.Interpolate(snp, snp.Frequencies, interpolateIn: MatrixType.Z);
        Assert.Equal(MatrixType.Z, r.Type);
    }

    // ================================================================
    //  Out-of-range: clamp produces same result as endpoint
    // ================================================================

    [Fact]
    public void OutOfRange_Clamp_MatchesEndpoint()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));

        // Get the stored first and last frequency
        double fFirst = snp.Frequencies[0];
        double fLast  = snp.Frequencies[snp.FrequencyCount - 1];

        // Interpolate at points beyond both ends (clamp policy)
        var warnings = new List<string>();
        RFNetwork.OnWarning += warnings.Add;
        try
        {
            var r = RFNetwork.Interpolate(snp,
                new[] { fFirst - 1e9, fLast + 1e9 },
                outOfRange: OutOfRangePolicy.WarnClamp);

            // Clamp: should match the first and last stored matrix
            for (int p = 0; p < snp.Ports; p++)
            for (int q = 0; q < snp.Ports; q++)
            {
                double errFirst = (r.Matrices[0][p, q] - snp.Matrices[0][p, q]).Magnitude;
                double errLast  = (r.Matrices[1][p, q]
                                 - snp.Matrices[snp.FrequencyCount - 1][p, q]).Magnitude;
                Assert.True(errFirst < 1e-10,
                    $"Clamped-below [{p},{q}] mismatch: {errFirst:G4}");
                Assert.True(errLast < 1e-10,
                    $"Clamped-above [{p},{q}] mismatch: {errLast:G4}");
            }
        }
        finally
        {
            RFNetwork.OnWarning -= warnings.Add;
        }

        // The warning should have fired
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void OutOfRange_Extrapolate_EmitsWarning()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        double fLast = snp.Frequencies[snp.FrequencyCount - 1];

        var warnings = new List<string>();
        RFNetwork.OnWarning += warnings.Add;
        try
        {
            RFNetwork.Interpolate(snp, new[] { fLast + 5e8 },
                outOfRange: OutOfRangePolicy.WarnExtrapolate);
        }
        finally
        {
            RFNetwork.OnWarning -= warnings.Add;
        }

        Assert.NotEmpty(warnings);
        // Sterner message about non-physical S-parameters
        Assert.Contains("non-physical", warnings[0]);
    }

    // ================================================================
    //  Phase unwrap: MagPhase interpolation on delay line
    //  Phase must unwrap before spline, not after
    // ================================================================

    [Fact]
    public void MagPhase_AtOwnPoints_Exact()
    {
        // 2-port delay line, phase varies smoothly
        double tau = 0.1e-9;
        double[] xs = { 0.5e9, 1.0e9, 1.5e9, 2.0e9, 2.5e9, 3.0e9 };
        var snp = MakeDelayLine(xs, tau);

        var interp = RFNetwork.Interpolate(snp, xs,
            format: InterpolationFormat.MagPhase);

        double rms = RFNetwork.CompareRMSValue(snp, interp);
        Assert.True(rms < 1e-10,
            $"MagPhase at-own-points RMS={rms:G4}, expected < 1e-10");
    }

    // ================================================================
    //  SNP.FromYSweep factory
    // ================================================================

    [Fact]
    public void FromYSweep_ReturnsS_MatchingManualConversion()
    {
        var s = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        var y = RFNetwork.SToY(s);

        // Use the factory
        var fromFactory = SNP.FromYSweep(y.Frequencies, y.Matrices, y.Z0);

        // Manual conversion for comparison
        var manual = RFNetwork.YToS(y);

        Assert.Equal(MatrixType.S, fromFactory.Type);
        double rms = RFNetwork.CompareRMSValue(fromFactory, manual);
        Assert.True(rms < 1e-14,
            $"FromYSweep vs YToS RMS={rms:G4}, expected < 1e-14");
    }

    [Fact]
    public void FromYSweep_RoundTrip_MatchesOriginalS()
    {
        var s = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        var y = RFNetwork.SToY(s);
        var sBack = SNP.FromYSweep(y.Frequencies, y.Matrices, y.Z0);

        double rms = RFNetwork.CompareRMSValue(s, sBack);
        Assert.True(rms < 1e-10,
            $"S→Y→SNP.FromYSweep round-trip RMS={rms:G4}, expected < 1e-10");
    }
}
