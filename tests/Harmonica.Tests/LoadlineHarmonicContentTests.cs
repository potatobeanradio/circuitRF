using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-harmonicarf-r3b §5 — "I suspect that many more harmonics are being used to calculate the
/// time domain [loadline] than K=3 displays." Confirms this by measurement (a DFT of the drawn
/// arrays), audits the rest of the K path, and reports — this is the owner's decision to make
/// (truncate the current axis to K, or leave it as the true device response), not a code change.
/// </summary>
[Collection("HarmonicaBenchmarks")]
public sealed class LoadlineHarmonicContentTests(ITestOutputHelper output)
{
    private const string I1Expr = "_v1/50";
    private const string I2Expr =
        "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
        "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2";

    private static CircuitModel DefaultModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = I1Expr,
                ["I[2,0]"] = I2Expr,
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34, PinStepDbm = 1.0,
        },
    };

    /// <summary>A plain DFT — the sample count here (64) is small enough that an O(N^2) direct
    /// transform costs nothing and needs no FFT-library dependency for a one-off diagnostic.</summary>
    private static double[] DftMagnitude(double[] x)
    {
        int n = x.Length;
        int bins = n / 2 + 1;
        var mag = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            double re = 0, im = 0;
            for (int t = 0; t < n; t++)
            {
                double theta = -2.0 * Math.PI * k * t / n;
                re += x[t] * Math.Cos(theta);
                im += x[t] * Math.Sin(theta);
            }
            mag[k] = Math.Sqrt(re * re + im * im) / n * (k == 0 ? 1 : 2);   // single-sided amplitude
        }
        return mag;
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void LoadlineHarmonicContent_AtCompression_ReportedForTheOwner()
    {
        var model = DefaultModel();
        var ctx = HarmonicaContext.Create(model);
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load, 1, new Complex(50, 0));

        var sweep = PinSearch.Sweep(ctx, terms, model.Settings.PinStartDbm, model.Settings.PinMaxDbm, model.Settings.PinStepDbm);
        Assert.True(sweep.Compressed, "fixture must reach compression to have a loadline worth DFT-ing");
        var at = sweep.AtCompression!;

        int k = model.Settings.HarmonicCount;
        int samples = model.Settings.LoadlineSamples is > 0 ? model.Settings.LoadlineSamples : 64;
        var (vds, ids) = IntrinsicPlane.Loadline(ctx.DutComponent, at.Point.V, ctx.Interface.DeviceNodes,
                                                 k, samples, ctx.IntrinsicPorts.DrainPort, ctx.IntrinsicPorts.SourcePort);

        var vdsMag = DftMagnitude(vds);
        var idsMag = DftMagnitude(ids);

        output.WriteLine($"═══ shipped default at compression (Pin={at.PavlDbm:F1} dBm), K={k}, {samples} samples ═══");
        output.WriteLine("");
        output.WriteLine("bin |   Vds mag (V)  | Vds/fund (%) |   Ids mag (A)  | Ids/fund (%)");
        double vFund = vdsMag[1], iFund = idsMag[1];
        for (int h = 0; h <= Math.Min(8, vdsMag.Length - 1); h++)
        {
            output.WriteLine($"{h,3} | {vdsMag[h],14:E4} | {(h == 0 ? double.NaN : 100.0 * vdsMag[h] / vFund),12:F4} | " +
                             $"{idsMag[h],14:E4} | {(h == 0 ? double.NaN : 100.0 * idsMag[h] / iFund),12:F4}");
        }

        // Confirm Vds is band-limited to K — the truncated Fourier series ResampleSpectrum evaluates
        // carries harmonics 0..K and NOTHING above, to round-off.
        double worstVdsAboveK = 0;
        for (int h = k + 1; h < vdsMag.Length; h++) worstVdsAboveK = Math.Max(worstVdsAboveK, vdsMag[h] / vFund);
        output.WriteLine("");
        output.WriteLine($"Vds content above bin {k}, relative to fundamental: {worstVdsAboveK:E3} (expect ~round-off, i.e. genuinely zero)");

        // Confirm Ids is NOT band-limited — dut.Evaluate is the device's full nonlinear response at a
        // band-limited voltage, so it legitimately contains harmonics the K=3 solve itself truncates.
        double worstIdsAboveK = 0;
        int worstIdsAboveKBin = -1;
        for (int h = k + 1; h < idsMag.Length; h++)
        {
            double rel = idsMag[h] / iFund;
            if (rel > worstIdsAboveK) { worstIdsAboveK = rel; worstIdsAboveKBin = h; }
        }
        output.WriteLine($"Ids content above bin {k}: largest is bin {worstIdsAboveKBin} at " +
                         $"{100.0 * worstIdsAboveK:F2}% of the fundamental — this is the owner's own suspicion, CONFIRMED.");

        Assert.True(worstVdsAboveK < 1e-6, $"Vds should be exactly band-limited to K={k}; measured {worstVdsAboveK:E3}");
        Assert.True(worstIdsAboveK > 0.001, "Ids should show real content above K — if this ever reads " +
            "~zero the device law itself changed shape and the owner's report would need re-checking");
    }
}
