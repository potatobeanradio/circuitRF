using System.Numerics;
using CircuitRF.Engine.Em3d;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-em3d-10 §6 gates 1, 2 and 6 — the comparison's arithmetic and its notes, with nothing
// installed. Gates 3-5, 7 and 8 are tests/Ui.Tests/Em3d/RunBothTests.cs.
public sealed class Em3dComparisonTests
{
    private const double Um = 1e-6;

    /// <summary>Gate 1: dMag, dPhase (wrapped to (−180, 180]) and dVec, each against values computed
    /// here, on two hand-built 2-port sets — including the wrap across ±180° and both ends of it.</summary>
    [Fact]
    public void Gate1_Differences_MatchHandComputedValues_AndPhaseWrapsInto_Minus180_180()
    {
        double[] f = [1e9, 2e9, 3e9];
        var p = new Complex[3 * 4];
        var o = new Complex[3 * 4];
        for (int k = 0; k < p.Length; k++) { p[k] = new Complex(0.3, 0.1); o[k] = new Complex(0.3, 0.1); }
        // S21 (i = 2, j = 1 → element [k, 1, 0]) at each frequency:
        p[0 * 4 + 2] = Complex.FromPolarCoordinates(0.5, Deg(179));  o[0 * 4 + 2] = Complex.FromPolarCoordinates(0.25, Deg(-179));
        p[1 * 4 + 2] = new Complex(-1, 0);                           o[1 * 4 + 2] = new Complex(1, 0);
        p[2 * 4 + 2] = new Complex(-1, -0.0);                        o[2 * 4 + 2] = new Complex(1, 0);

        var (data, refusal) = Em3dComparison.Differences(Cube(f, p), Cube(f, o));
        Assert.Null(refusal);
        double[] mag = data![Em3dComparison.DMagCube].RealValues, phase = data[Em3dComparison.DPhaseCube].RealValues,
                 vec = data[Em3dComparison.DVecCube].RealValues;

        Assert.Equal(20 * Math.Log10(2), mag[2], 12);
        Assert.Equal(-2, phase[2], 9);                        // 179 − (−179) = 358 → −2
        Assert.Equal((p[2] - o[2]).Magnitude, vec[2], 12);
        Assert.Equal(180, phase[1 * 4 + 2], 12);               // exactly +180 stays
        Assert.Equal(180, phase[2 * 4 + 2], 12);               // −180 wraps to +180
        Assert.Equal(2, vec[1 * 4 + 2], 12);
        Assert.All(new[] { 0, 1, 3 }, e => Assert.Equal(0, mag[e]));
        Assert.Equal(p, data[Em3dComparison.PalaceCube].ComplexValues);
        Assert.Equal(o, data[Em3dComparison.OpenEmsCube].ComplexValues);

        string summary = Em3dComparison.Summary(data);
        Assert.StartsWith("Palace against openEMS: S21 by at most 6.02 dB (at 1 GHz) and 180° (at 2 GHz)", summary);
        Assert.Contains("; S11 by at most 0 dB", summary);
    }

    /// <summary>Gate 2: frequencies differing by 1 Hz at one point refuse, naming it — never interpolate.</summary>
    [Fact]
    public void Gate2_FrequenciesDifferingBy1HzAtOnePoint_AreRefused()
    {
        var s = Enumerable.Repeat(new Complex(0.1, 0.2), 3 * 4).ToArray();
        var (data, refusal) = Em3dComparison.Differences(Cube([1e9, 2e9, 3e9], s), Cube([1e9, 2e9 + 1, 3e9], s));
        Assert.Null(data);
        Assert.Contains("frequency 2 differs", refusal);
        Assert.Contains("does not interpolate", refusal);
    }

    /// <summary>Gate 6: a note appears only when its fact is true of the run — the lossy-dielectric,
    /// PEC and thin-wire notes and the unconverged warning are absent on a lossless run with none of
    /// them, and present on a lossy one that has them.</summary>
    [Fact]
    public void Gate6_NotesApplyOnlyWhenTrue()
    {
        var s = Enumerable.Repeat(new Complex(0.1, 0.2), 2 * 4).ToArray();
        double[] f = [1e9, 2e9];
        var none = new Em3dComparisonFacts(1.5e9, [], [], []);
        var some = new Em3dComparisonFacts(1.5e9, ["via1"], ["wire1"], [2]);

        var lossless = Em3dComparison.Compare(Cube(f, s), Cube(f, s), Microstrip(tanD: 0), none);
        Assert.DoesNotContain(lossless.Notes, n => n.Contains("lossy", StringComparison.Ordinal));
        Assert.DoesNotContain(lossless.Notes, n => n.Contains("perfect conductors", StringComparison.Ordinal));
        Assert.DoesNotContain(lossless.Notes, n => n.Contains("thin conductor", StringComparison.Ordinal));
        Assert.Empty(lossless.Warnings);
        Assert.Contains(Em3dComparison.CrossCheckSentence, lossless.Notes);

        var lossy = Em3dComparison.Compare(Cube(f, s), Cube(f, s), Microstrip(tanD: 0.004), some);
        Assert.Contains(lossy.Notes, n => n.StartsWith("'Alumina' is lossy", StringComparison.Ordinal) && n.Contains("1.5 GHz", StringComparison.Ordinal));
        Assert.Contains(lossy.Notes, n => n.Contains("perfect conductors", StringComparison.Ordinal) && n.Contains("'via1'", StringComparison.Ordinal));
        Assert.Contains(lossy.Notes, n => n.Contains("thin conductor", StringComparison.Ordinal) && n.Contains("'wire1'", StringComparison.Ordinal));
        Assert.Contains("port 2 did not reach", Assert.Single(lossy.Warnings));

        // The notes travel in the DataSet: the summary first, then the warning, each an axis label.
        var carried = lossy.Data!.CubesIn(Em3dComparison.Group)[Em3dComparison.NotesCube];
        Assert.Equal(lossy.Summary, carried.Axes[0].Labels![0]);
        Assert.Equal([0.0, 1.0], carried.RealValues[..2]);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static double Deg(double d) => d * Math.PI / 180;

    private static DataCube Cube(double[] f, Complex[] s)
        => new([new Axis("freq", f, "Hz"), new Axis("i", [1, 2], "port"), new Axis("j", [1, 2], "port")], s);

    /// <summary>A strip on a substrate of loss tangent <paramref name="tanD"/>, two lumped ports.</summary>
    private static Em3dProblem Microstrip(double tanD)
    {
        var faces = new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                  Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing);
        var box = new Em3dAirBox(new Point3(-1500 * Um, -2000 * Um, 0), new Point3(1500 * Um, 2000 * Um, 1254 * Um), faces);
        Em3dPort Port(int n, double y) => new(n, $"P{n}", "strip", "airbox/zmin", new Point3(-300 * Um, y, 0),
            new Point3(300 * Um, y, 254 * Um), new Point3(0, 0, 1), new Complex(50, 0),
            new Em3dReferencePlane(new Point3(0, y, 127 * Um), new Point3(0, 1, 0), 0));
        return new Em3dProblem(
            [new Em3dSolid("substrate", "Alumina", Em3dRole.Dielectric,
                           new Em3dBox(new Point3(-1500 * Um, -2000 * Um, 0), new Point3(1500 * Um, 2000 * Um, 254 * Um)), 1)],
            [new Em3dSheet("strip", "Copper",
                           [new Point2(-300 * Um, -2000 * Um), new Point2(300 * Um, -2000 * Um), new Point2(300 * Um, 2000 * Um), new Point2(-300 * Um, 2000 * Um)],
                           [], 254 * Um, 35 * Um, 2)],
            [new("Air", 1, null, 0, 1, 0), new("Alumina", 9.8, null, tanD, 1, 0), new("Copper", 1, null, 0, 1, 5.8e7)],
            [Port(1, -2000 * Um), Port(2, 2000 * Um)],
            box, new Em3dFrequency(1e9, 2e9, 2, Em3dSweepKind.Linear), 20);
    }
}
