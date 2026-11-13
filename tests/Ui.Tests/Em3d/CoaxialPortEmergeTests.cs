// ================================================================
//  CoaxialPortEmergeTests.cs — brief-cxp-3 gate 2, solved: CoaxialPortTests' 20 mm coax on EMerge, its two rings run as
//  circuitRF's radial lumped port (circuitrf_emerge.RadialLumpedPort). On the air box's PEC faces the line is matched, loses
//  its copper's closed form and delays by βℓ, as on Palace; inside the box (an open coax end with air behind each ring) it
//  agrees with Palace on the same geometry — which is also Palace's gate for a ring inside the model, where the pin's end
//  cap had been claimed into the port (GmshGeoWriter). Benchmark tier: EMerge about 15 s a run, Palace about 2 min.
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
public sealed class CoaxialPortEmergeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cxp3-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static double Wrap(double deg) { deg %= 360; return deg > 180 ? deg - 360 : deg <= -180 ? deg + 360 : deg; }

    private (double[] GHz, Complex[] S11, Complex[] S21) Run(string solver, long endPaddingUm, int? order = null)
    {
        string path = CoaxialPortTests.Save(_root, CoaxialPortTests.Coax(C3dPlane.YZ, 1, 10, 3, boolean: true, order: order, solver: solver,
                                                                          endPaddingUm: endPaddingUm));
        var doc = C3dPersistence.LoadFromFile(path);
        var embedded = C3dSetups.Read(doc).Single().Setup!;
        var wall = Stopwatch.StartNew();
        var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded, path), doc, path, null,
                                                Path.Combine(_root, $"results-{solver}-{endPaddingUm}"), confirmMemory: _ => true);
        output.WriteLine($"{solver}, end padding {endPaddingUm} µm: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s");
        foreach (var w in result.Warnings) output.WriteLine("  note: " + w);
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        var snp = TouchstoneIO.ReadFile(result.SnpPath!);
        var r = (snp.Frequencies.Select(f => f / 1e9).ToArray(), snp.Matrices.Select(m => m[0, 0]).ToArray(), snp.Matrices.Select(m => m[1, 0]).ToArray());
        for (int k = 0; k < r.Item1.Length; k++)
            output.WriteLine($"  {r.Item1[k],5:F2} GHz  |S11| {20 * Math.Log10(r.Item2[k].Magnitude),8:F2} dB  |S21| {20 * Math.Log10(r.Item3[k].Magnitude),8:F4} dB  " +
                             $"∠S21 {r.Item3[k].Phase * 180 / Math.PI,8:F2}°");
        return r;
    }

    [EmergeFact]
    public void Gate2_OnTheBoxsFaces_AMatchedCoaxIsMatched_LosesItsCopper_AndDelaysByBetaL()
    {
        var (ghz, s11, s21) = Run("Emerge", 0);
        for (int k = 0; k < ghz.Length; k++)
        {
            double hz = ghz[k] * 1e9;
            double m11 = 20 * Math.Log10(s11[k].Magnitude), m21 = 20 * Math.Log10(s21[k].Magnitude);
            output.WriteLine($"{ghz[k],5:F2} GHz: closed form |S21| {CoaxialPortPalaceTests.ClosedFormS21Db(hz):F4} dB, −βℓ {CoaxialPortPalaceTests.ClosedFormS21Deg(hz):F2}°");
            Assert.True(m11 < -25, $"|S11| at {ghz[k]} GHz is {m11:F2} dB");
            Assert.True(Math.Abs(m21 - CoaxialPortPalaceTests.ClosedFormS21Db(hz)) < 0.05, $"|S21| at {ghz[k]} GHz is {m21:F4} dB");
            Assert.True(Math.Abs(Wrap(s21[k].Phase * 180 / Math.PI - CoaxialPortPalaceTests.ClosedFormS21Deg(hz))) < 2, $"∠S21 at {ghz[k]} GHz");
        }
    }

    [PalaceAndEmergeFact]
    public void Gate2_InsideTheBox_EMergeAgreesWithPalace_OnTheSameOpenEndedCoax()
    {
        var (ghz, e11, e21) = Run("Emerge", 500);
        var (_, p11, p21) = Run("Palace", 500, order: 2);
        for (int k = 0; k < ghz.Length; k++)
        {
            double d11 = 20 * Math.Log10(e11[k].Magnitude / p11[k].Magnitude), d21 = 20 * Math.Log10(e21[k].Magnitude / p21[k].Magnitude);
            output.WriteLine($"{ghz[k],5:F2} GHz: EMerge − Palace |S11| {d11:F3} dB, |S21| {d21:F4} dB, ∠S21 {Wrap((e21[k].Phase - p21[k].Phase) * 180 / Math.PI):F2}°");
            Assert.True(Math.Abs(d21) < 0.1, $"|S21| at {ghz[k]} GHz differs by {d21:F4} dB");
            Assert.True(Math.Abs(Wrap((e21[k].Phase - p21[k].Phase) * 180 / Math.PI)) < 2, $"∠S21 at {ghz[k]} GHz");
        }
    }
}
