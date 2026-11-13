// ================================================================
//  CoaxialPortPalaceTests.cs — brief-cxp-1 gates 3 and 4, solved: CoaxialPortTests' 20 mm coax (a 0.4 mm pin in a 1.34 mm
//  PTFE bore, εr 2.1, copper), a coaxial port at each end on the air box's PEC faces, run on Palace at 1, 5.5 and 10 GHz.
//  A matched line is matched (|S11|), loses what its copper's closed form says (|S21|), and delays by βℓ (∠S21); the same coax
//  along x, y and z gives the same answer. At element order 2: Draft's first order is ~4° long at 10 GHz over 20 mm, which is
//  the line's own dispersion and not the port's (src/Design/RESOLVED.md, brief-cxp-1). Benchmark tier: Palace, three solves of
//  about 40 s each.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Tests.ThreeD;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

[Trait("Category", "Benchmark")]
public sealed class CoaxialPortPalaceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cxp1p-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private const double C0 = 299_792_458.0, Mu0 = 4e-7 * Math.PI, Eta0 = 376.730313668;
    private const double Er = 2.1, Sigma = 5.8e7;

    /// <summary>|S21| of the line in dB from its copper alone: α = Rs/(2η·ln(b/a))·(1/a + 1/b), Rs = √(πfμ₀/σ).</summary>
    internal static double ClosedFormS21Db(double hz)
    {
        double a = CoaxialPortTests.PinUm * 1e-6, b = CoaxialPortTests.BoreUm * 1e-6, len = CoaxialPortTests.LengthUm * 1e-6;
        double rs = Math.Sqrt(Math.PI * hz * Mu0 / Sigma), eta = Eta0 / Math.Sqrt(Er);
        double alpha = rs / (2 * eta * Math.Log(b / a)) * (1 / a + 1 / b);
        return -20 / Math.Log(10) * alpha * len;
    }

    /// <summary>∠S21 in degrees for a TEM line in the PTFE, wrapped to (−180, 180].</summary>
    internal static double ClosedFormS21Deg(double hz)
        => Wrap(-2 * Math.PI * hz * Math.Sqrt(Er) / C0 * CoaxialPortTests.LengthUm * 1e-6 * 180 / Math.PI);

    private static double Wrap(double deg) { deg %= 360; return deg > 180 ? deg - 360 : deg <= -180 ? deg + 360 : deg; }

    private (double[] GHz, Complex[] S11, Complex[] S21) Run(C3dPlane plane)
    {
        string path = CoaxialPortTests.Save(_root, CoaxialPortTests.Coax(plane, 1, 10, 3, boolean: true, order: 2));
        var doc = C3dPersistence.LoadFromFile(path);
        var embedded = C3dSetups.Read(doc).Single().Setup!;
        var wall = Stopwatch.StartNew();
        var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded, path), doc, path, null, Path.Combine(_root, "results-" + plane),
                                                confirmMemory: _ => true);
        output.WriteLine($"{plane}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s");
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        var snp = TouchstoneIO.ReadFile(result.SnpPath!);
        return ([.. snp.Frequencies.Select(f => f / 1e9)], [.. snp.Matrices.Select(m => m[0, 0])], [.. snp.Matrices.Select(m => m[1, 0])]);
    }

    [PalaceFact]
    public void Gates3And4_AMatchedCoax_IsMatched_LosesItsCopper_DelaysByBetaL_InEveryOrientation()
    {
        var runs = new[] { C3dPlane.YZ, C3dPlane.XZ, C3dPlane.XY }.Select(p => (Plane: p, R: Run(p))).ToList();
        foreach (var (plane, (ghz, s11, s21)) in runs)
            for (int k = 0; k < ghz.Length; k++)
                output.WriteLine($"{plane} {ghz[k],5:F2} GHz  |S11| {20 * Math.Log10(s11[k].Magnitude),8:F2} dB  |S21| " +
                                 $"{20 * Math.Log10(s21[k].Magnitude),8:F4} dB (closed form {ClosedFormS21Db(ghz[k] * 1e9),8:F4})  " +
                                 $"∠S21 {s21[k].Phase * 180 / Math.PI,8:F2}° (−βℓ {ClosedFormS21Deg(ghz[k] * 1e9),8:F2}°)");
        foreach (var (plane, (ghz, s11, s21)) in runs)
            for (int k = 0; k < ghz.Length; k++)
            {
                double hz = ghz[k] * 1e9;
                double m11 = 20 * Math.Log10(s11[k].Magnitude), m21 = 20 * Math.Log10(s21[k].Magnitude);
                double ph = s21[k].Phase * 180 / Math.PI;
                Assert.True(m11 < -30, $"{plane}: |S11| at {ghz[k]} GHz is {m11:F2} dB");
                Assert.True(Math.Abs(m21 - ClosedFormS21Db(hz)) < 0.02, $"{plane}: |S21| at {ghz[k]} GHz is {m21:F4} dB");
                Assert.True(Math.Abs(Wrap(ph - ClosedFormS21Deg(hz))) < 1, $"{plane}: ∠S21 at {ghz[k]} GHz is {ph:F2}°");
            }
        // gate 4 — the orientation does not matter
        var (_, (_, x11, x21)) = runs[0];
        foreach (var (plane, (ghz, s11, s21)) in runs.Skip(1))
            for (int k = 0; k < ghz.Length; k++)
            {
                Assert.True(Math.Abs(20 * Math.Log10(s21[k].Magnitude / x21[k].Magnitude)) < 0.01, $"{plane} vs YZ: |S21| at {ghz[k]} GHz");
                Assert.True(Math.Abs(Wrap((s21[k].Phase - x21[k].Phase) * 180 / Math.PI)) < 0.1, $"{plane} vs YZ: ∠S21 at {ghz[k]} GHz");
            }
    }
}
