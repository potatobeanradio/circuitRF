using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The circulator's PER-PORT match detune (owner request, 2026-08-31): <c>VSWR1..3</c> with
/// <c>Ang1..3</c>, so a power amplifier on port 1 can be made to see a stated mismatch at a stated
/// angle — which is what a real circulator presents and what a single return loss cannot say.
///
/// <para><b>What is actually gated here.</b> That the VSWR and angle a user types become that port's
/// own <c>S_pp</c> and nothing else's; that <c>VSWR = 1</c> means "not stated" so the isotropic
/// <c>RL</c> still governs, which is what keeps every existing design unchanged; and that the
/// detune is a DIAGONAL entry, so the forward and reverse paths are untouched. The end-to-end half —
/// the impedance an amplifier connected to port 1 actually sees — needs a solve and lives in
/// <c>tests/Engine.Tests/Devices/CirculatorDetuneSParamTests.cs</c>.</para>
///
/// <para>Every expected number is computed here from the VSWR and the angle, never read back out of
/// the model: the trip from "VSWR 2.0 at 135°" to a complex amplitude is the whole thing under
/// test.</para>
/// </summary>
public class CirculatorDetuneTests(ITestOutputHelper output)
{
    private static double Rad(double deg) => deg * Math.PI / 180.0;

    private static CirculatorModel Circ(double rlDb = 200.0,
                                        double[]? vswr = null, double[]? angDeg = null)
        => new(CirculatorDirection.CW, ilDb: 0, isolationDb: 200, returnLossDb: rlDb, z0: 50,
               pimDbm: -200.0, pimPcDbm: 43.0,
               vswr: vswr,
               angRad: angDeg is null ? null : [.. angDeg.Select(Rad)]);

    private static void Near(Complex expected, Complex actual, double tol = 1e-12)
        => Assert.True((expected - actual).Magnitude < tol,
                       $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Magnitude:G6})");

    [Theory]
    [InlineData(2.0,   135.0)]
    [InlineData(1.25,    0.0)]
    [InlineData(1.5,   -90.0)]
    [InlineData(3.0,   180.0)]
    public void AStatedVswrAndAngleBecomeThatPortsOwnSpp(double vswr, double angDeg)
    {
        var s = Circ(vswr: [vswr, 1, 1], angDeg: [angDeg, 0, 0]).SAt(2 * Math.PI * 1e9);

        var expected = Complex.FromPolarCoordinates((vswr - 1.0) / (vswr + 1.0), Rad(angDeg));
        output.WriteLine($"VSWR {vswr} at {angDeg}° -> S11 = {s[0, 0]} (|Γ| = {s[0, 0].Magnitude:G6})");

        Near(expected, s[0, 0]);

        // The other two ports are untouched, and so is every off-diagonal entry: this is a
        // reflection, not a change to what the component does.
        Near(Complex.Zero, s[1, 1]);
        Near(Complex.Zero, s[2, 2]);
        Near(Complex.One,  s[1, 0]);   // CW: 1 -> 2
        Near(Complex.One,  s[2, 1]);
        Near(Complex.One,  s[0, 2]);
        Near(Complex.Zero, s[0, 1]);   // isolation off means the entry is ABSENT
    }

    /// <summary>
    /// Each port carries its OWN pair. A three-port with one number per port is only useful if the
    /// three do not leak into each other, and a shared array indexed wrongly would still pass a
    /// single-port test.
    /// </summary>
    [Fact]
    public void EachPortCarriesItsOwnVswrAndAngle()
    {
        var s = Circ(vswr: [2.0, 1.5, 1.25], angDeg: [30.0, -60.0, 170.0]).SAt(2 * Math.PI * 1e9);

        Near(Complex.FromPolarCoordinates(1.0 / 3.0, Rad(30.0)),   s[0, 0]);
        Near(Complex.FromPolarCoordinates(0.2,       Rad(-60.0)),  s[1, 1]);
        Near(Complex.FromPolarCoordinates(1.0 / 9.0, Rad(170.0)),  s[2, 2]);
    }

    /// <summary>
    /// <c>VSWR = 1</c> is "this port did not state one" and falls back to <c>RL</c> — the reason
    /// every design that predates these parameters is unchanged, and the reason the datasheet form
    /// (one return loss for the whole part) still works.
    /// </summary>
    [Fact]
    public void VswrOfOneMeansNotStatedAndTheReturnLossStillGoverns()
    {
        double rho = Math.Pow(10.0, -20.0 / 20.0);          // 20 dB return loss

        var mixed = Circ(rlDb: 20.0, vswr: [2.0, 1, 1], angDeg: [90.0, 0, 0]).SAt(0.0);
        Near(Complex.FromPolarCoordinates(1.0 / 3.0, Math.PI / 2.0), mixed[0, 0]);
        Near(new Complex(rho, 0), mixed[1, 1]);
        Near(new Complex(rho, 0), mixed[2, 2]);

        // And with nothing stated anywhere, it is exactly the component it was before.
        var untouched = Circ(rlDb: 20.0).SAt(0.0);
        for (int p = 0; p < 3; p++) Near(new Complex(rho, 0), untouched[p, p]);

        // An angle with no VSWR beside it changes nothing — it is read only where a mismatch was
        // stated, so a user cannot half-state one and get a silent partial answer.
        var angleOnly = Circ(rlDb: 20.0, angDeg: [90.0, 90.0, 90.0]).SAt(0.0);
        for (int p = 0; p < 3; p++) Near(new Complex(rho, 0), angleOnly[p, p]);
    }

    /// <summary>
    /// The detune is frequency-FLAT, deliberately (it is the mismatch a user wants to test a PA
    /// against, not a rotating one) — and that is what keeps the block memoryless, so the
    /// passive-intermod overlay can still sit on top of a complex S.
    /// </summary>
    [Fact]
    public void TheDetuneIsFlatWithFrequencyAndSurvivesThePimOverlay()
    {
        var m = Circ(vswr: [2.0, 1, 1], angDeg: [135.0, 0, 0]);
        var expected = Complex.FromPolarCoordinates(1.0 / 3.0, Rad(135.0));

        foreach (double f in (double[])[0.0, 1e6, 1e9, 40e9]) Near(expected, m.SAt(2 * Math.PI * f)[0, 0]);

        var withPim = new CirculatorModel(CirculatorDirection.CW, 0, 200, 200, 50,
                                          pimDbm: -80.0, pimPcDbm: 43.0,
                                          vswr: [2.0, 1, 1], angRad: [Rad(135.0), 0, 0]);
        Assert.Equal(ModelKind.Nonlinear, withPim.Kind);
        Assert.NotNull(withPim.Pim);
        Near(expected, withPim.SAt(2 * Math.PI * 1e9)[0, 0]);
    }

    /// <summary>
    /// The netlist spelling, through the factory: <c>Ang</c> arrives in RADIANS because the
    /// Elaborator has already applied the parameter's own <c>deg</c> unit — the convention TLIN's
    /// <c>E</c> established, and the one place a hand-written <c>Ang1=135</c> would otherwise mean
    /// 135 radians.
    /// </summary>
    [Fact]
    public void TheFactoryReadsTheSixParametersFromANetlist()
    {
        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["VSWR1"] = new Value(2.0),
            ["Ang1"]  = new Value(Rad(135.0)),
            ["VSWR3"] = new Value(1.5),
        };

        var m = Assert.IsType<CirculatorModel>(ComponentModelFactory.TryCreate("Circulator", parameters));
        var s = m.SAt(2 * Math.PI * 1e9);

        Near(Complex.FromPolarCoordinates(1.0 / 3.0, Rad(135.0)), s[0, 0]);
        Near(Complex.Zero,                                        s[1, 1]);
        Near(new Complex(0.2, 0),                                 s[2, 2]);
    }
}
