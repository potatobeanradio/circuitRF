using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-141 gate 5 — a Palace wave port's S is referred to a mode whose voltage along the port's path is real.
//
//  The row is Palace's own 10 GHz output for testdata/em-psv/via-two-dielectrics (port 1 a line on the buried level, port 2
//  one on the top surface, two ungrouped wave ports), as port-S.csv, port-V.csv and port-Z.csv printed it. Nothing is solved.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class PalaceWavePortPhaseTests
{
    private const double Z0 = 50;

    // port-S.csv: |S| dB and arg S degrees, row = port, column = excitation.
    private static readonly Complex[,] S =
    {
        { Db(-9.311669987251, -73.40166532107), Db(-0.5669949713647, -45.18173264167) },
        { Db(-0.5637096562156, -65.25137479485), Db(-9.391637904583, 143.9889496288) },
    };

    // port-V.csv: V_wp[i][j], port i's path with port j driven.
    private static readonly Complex[,] V =
    {
        { new(6.790422070647, -3.385561055494), new(3.530513048401, -5.091392885526) },
        { new(3.776704255833, -8.041942659801), new(6.844418320476, 1.839306203693) },
    };

    // port-Z.csv: Z_PV per port.
    private static readonly Complex[] ZPv = [new(43.85974700758, 0), new(90.22588106091, 0)];

    private static readonly Em3dPort[] Ports = [Wave(1), Wave(2)];

    [Fact]
    public void TheRotationMakesTheRecordedRowReciprocal()
    {
        var published = Published([S], [V]);
        double before = WavePortReferences.Reciprocity([S]);
        double after = WavePortReferences.Reciprocity(published);
        Assert.True(before > 0.3, $"Palace's row: |S21 − S12| {before}");
        Assert.True(after <= 0.01, $"published: |S21 − S12| {after}");
    }

    /// <summary>The terminal transform's one-entry face (an ungrouped port in a run with a terminal group) takes the same
    /// phase: two one-entry faces give what the ungrouped path publishes.</summary>
    [Fact]
    public void TheTransformsOneEntryFaceTakesThePhase()
    {
        var r = ModalTerminalTransform.Solve(new ModalTerminalSample(S, V, [ZPv[0].Real, ZPv[1].Real], [Complex.One, Complex.One]),
                                             [[0], [1]], [Z0, Z0]);
        Assert.Null(r.Error);
        var expected = Published([S], [V])[0];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((r.S![i, j] - expected[i, j]).Magnitude <= 1e-12, $"S{i + 1}{j + 1}: {r.S[i, j]} against {expected[i, j]}");
    }

    [Fact]
    public void EqualPhasesLeaveTheRowBitIdentical()
    {
        var rotated = WavePortReferences.Rotate([S], [0, 1], [[0.3, 0.3]])[0];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(S[i, j].Real), BitConverter.DoubleToInt64Bits(rotated[i, j].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(S[i, j].Imaginary), BitConverter.DoubleToInt64Bits(rotated[i, j].Imaginary));
            }
    }

    /// <summary>Port 1 looking into a short (1 + S₁₁ → 0): its own excitation says nothing about its mode, and the fit takes
    /// the phase from port 2's, V₁₂ / S₁₂.</summary>
    [Fact]
    public void ANearShortTakesThePhaseFromTheOtherExcitation()
    {
        var shorted = (Complex[,])S.Clone();
        shorted[0, 0] = new Complex(-1 + 1e-9, 0);
        var fit = WavePortReferences.FitModeVoltages([shorted], [0, 1], [V]);
        Assert.Equal(1, fit.LeanedOn[0][0]);
        Assert.Equal(-1, fit.LeanedOn[0][1]);
        Assert.Equal((V[0, 1] / S[0, 1]).Phase, fit.Voltage[0][0].Phase, 1e-6);
    }

    private static Complex[][,] Published(Complex[][,] s, Complex[][,] v)
    {
        var theta = WavePortReferences.FitModeVoltages(s, [0, 1], v).Voltage.Select(r => r.Select(x => x.Phase).ToArray()).ToList();
        var rotated = new CircuitRF.Design.Em3d.PalacePortS([10e9], WavePortReferences.Rotate(s, [0, 1], theta));
        return WavePortReferences.Renormalise(rotated, Ports, Ports, [ZPv]).S;
    }

    private static Complex Db(double db, double deg) => Complex.FromPolarCoordinates(Math.Pow(10, db / 20), deg * Math.PI / 180);

    private static Em3dPort Wave(int n)
        => new(n, $"P{n}", "s", "g", default, default, default, Z0, new Em3dReferencePlane(default, default, 0)) { Kind = Em3dPortKind.Wave };
}
