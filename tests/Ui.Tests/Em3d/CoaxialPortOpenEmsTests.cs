// ================================================================
//  CoaxialPortOpenEmsTests.cs — brief-cxp-4 gate 2, solved: CoaxialPortTests' 20 mm coax with its rings inside the box (open
//  coax ends, 0.5 mm of air to PEC walls: openEMS refuses a ring on the wall itself), on openEMS at the 3D Wave Ports Launch's
//  grading (1.15), against Palace at element order 2 on the same geometry. openEMS writes every metal as a perfect conductor
//  (its grid does not resolve a skin depth) where Palace gives copper its conductivity, so |S21| is held to Palace's within the
//  line's copper loss and the staircase's mismatch. Benchmark tier: openEMS and Palace, a few minutes.
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
public sealed class CoaxialPortOpenEmsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cxp4-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static double Wrap(double deg) { deg %= 360; return deg > 180 ? deg - 360 : deg <= -180 ? deg + 360 : deg; }

    private (double[] GHz, Complex[] S11, Complex[] S21) Run(string solver, int? order = null, int? cellsPerWavelength = null, long lengthUm = CoaxialPortTests.LengthUm)
    {
        string path = CoaxialPortTests.Save(_root, CoaxialPortTests.Coax(C3dPlane.YZ, 1, 10, 3, boolean: true, order: order, solver: solver,
                                                                          endPaddingUm: 500, openEmsCellsPerWavelength: cellsPerWavelength,
                                                                          lengthUm: lengthUm));
        var doc = C3dPersistence.LoadFromFile(path);
        var embedded = C3dSetups.Read(doc).Single().Setup!;
        var wall = Stopwatch.StartNew();
        var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded, path), doc, path, null, Path.Combine(_root, $"results-{solver}-{lengthUm}-{cellsPerWavelength}"),
                                                confirmMemory: _ => true);
        output.WriteLine($"{solver}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s");
        foreach (var w in result.Warnings) output.WriteLine("  note: " + w);
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        var snp = TouchstoneIO.ReadFile(result.SnpPath!);
        var r = (snp.Frequencies.Select(f => f / 1e9).ToArray(), snp.Matrices.Select(m => m[0, 0]).ToArray(), snp.Matrices.Select(m => m[1, 0]).ToArray());
        for (int k = 0; k < r.Item1.Length; k++)
            output.WriteLine($"  {r.Item1[k],5:F2} GHz  |S11| {20 * Math.Log10(r.Item2[k].Magnitude),8:F2} dB  |S21| {20 * Math.Log10(r.Item3[k].Magnitude),8:F4} dB  " +
                             $"∠S21 {r.Item3[k].Phase * 180 / Math.PI,8:F2}°");
        return r;
    }

    /// <summary>
    /// The coax at 10 and 20 mm on each solver. A line of length ℓ between two ports reads −∠S21 = φ_ports + β·ℓ, so the
    /// second 10 mm gives the line's own β (the ports cancel) and 2·φ(10) − φ(20) the two ports' own phase. openEMS's staircased
    /// coax is not PTFE's (its εeff is reported, and the 3D Wave Ports example's notes say why), so the PORT is held to Palace's:
    /// the ports' own phase within 3.5°, |S11| below −20 dB, and |S21| within 0.1 dB of Palace's — openEMS's metal is perfect and
    /// Palace's is copper, which is under 0.06 dB over this line.
    /// </summary>
    [PalaceFact]
    public void Gate2_InsideTheBox_OpenEmsRingPortsMatchPalaces_TheLineAside()
    {
        var o10 = Run("OpenEms", lengthUm: 10000);
        var o20 = Run("OpenEms");
        var p10 = Run("Palace", order: 2, lengthUm: 10000);
        var p20 = Run("Palace", order: 2);
        for (int k = 0; k < o20.GHz.Length; k++)
        {
            double hz = o20.GHz[k] * 1e9, ideal = 2 * Math.PI * hz * Math.Sqrt(2.1) / 299792458.0 * 0.01 * 180 / Math.PI;
            double Lag(Complex s21, double expected) => expected - Wrap(expected + s21.Phase * 180 / Math.PI);   // −∠S21, unwrapped near expected
            double oSec = Lag(o20.S21[k] / o10.S21[k], ideal), pSec = Lag(p20.S21[k] / p10.S21[k], ideal);
            double oPorts = 2 * Lag(o10.S21[k], ideal) - Lag(o20.S21[k], 2 * ideal), pPorts = 2 * Lag(p10.S21[k], ideal) - Lag(p20.S21[k], 2 * ideal);
            double m11 = 20 * Math.Log10(o20.S11[k].Magnitude), d21 = 20 * Math.Log10(o20.S21[k].Magnitude / p20.S21[k].Magnitude);
            output.WriteLine($"{o20.GHz[k],5:F2} GHz: line εeff openEMS {2.1 * Math.Pow(oSec / ideal, 2):F3}, Palace {2.1 * Math.Pow(pSec / ideal, 2):F3}; " +
                             $"ports' own phase openEMS {oPorts:F2}°, Palace {pPorts:F2}°; openEMS |S11| {m11:F2} dB; |S21| openEMS − Palace {d21:F4} dB");
            Assert.True(m11 < -20, $"|S11| at {o20.GHz[k]} GHz is {m11:F2} dB");
            Assert.True(Math.Abs(d21) < 0.1, $"|S21| at {o20.GHz[k]} GHz differs by {d21:F4} dB");
            // 3.5°: the brief asked 3°; at 10 GHz the two read 3.1° apart, which follows the open coax ends rather than the ring
            // (src/Design/RESOLVED.md, brief-cxp-4), and the owner accepted that accuracy for shipping.
            Assert.True(Math.Abs(oPorts - pPorts) < 3.5, $"the ports' own phase at {o20.GHz[k]} GHz: openEMS {oPorts:F2}°, Palace {pPorts:F2}°");
        }
    }
}
