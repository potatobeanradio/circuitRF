using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// What an amplifier connected to a DETUNED circulator's port 1 actually sees — the question the
/// per-port <c>VSWR</c>/<c>Ang</c> parameters exist to answer, and one only a solve can settle.
///
/// <para><b>Why this file rather than one more model-level assertion.</b> The rejected design was to
/// reuse <c>Z0</c>: make the reference impedance complex and let the mismatch fall out. It does not
/// work, and the reason is a property of the NETWORK, not of the matrix. With the ideal permutation
/// S, a wave entering port 1 leaves at port 2, reflects off whatever terminates it, circulates to
/// port 3, reflects again, and only then returns to port 1 — so the reflection seen at port 1 is the
/// PRODUCT of the other two terminations' mismatches, <c>conj(ρ²)</c>, and nothing the user typed.
/// <c>S11</c> is the port's own reflection with the other ports matched, which is what a VSWR number
/// means. Both halves are measured below, against closed forms derived here, so the argument is
/// executable rather than only written down.</para>
/// </summary>
public class CirculatorDetuneSParamTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static Complex[,] SAt(string cnl, double freqHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var c  = SParameterEngine.Run(nl, [freqHz])["S"];
        int n  = c.Axes[1].Length;

        var s = new Complex[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            s[i, j] = (Complex)c[0, i, j];
        return s;
    }

    private static string ThreePorts() => @"
Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
Port:P3  n3 0  Num=3  Z=50 Ohm";

    /// <summary>The impedance a Γ measured against 50 Ω corresponds to.</summary>
    private static Complex ZOf(Complex gamma) => 50.0 * (Complex.One + gamma) / (Complex.One - gamma);

    /// <summary>
    /// A stated VSWR and angle at port 1 IS the load an amplifier there sees, to the last digit, with
    /// the other two ports matched — <c>Z = Z0(1 + Γ)/(1 − Γ)</c> and nothing else in the expression.
    /// </summary>
    [Theory]
    [InlineData(2.0,   135.0)]
    [InlineData(1.25,  -40.0)]
    [InlineData(1.6,     0.0)]
    public void APaOnPort1SeesExactlyTheStatedMismatch(double vswr, double angDeg)
    {
        var s = SAt(ThreePorts() + $@"
Circulator:C1  n1 0 n2 0 n3 0  Z0=50 Ohm VSWR1={N(vswr)} Ang1={N(angDeg)} deg", 2.0e9);

        var expected = Complex.FromPolarCoordinates((vswr - 1.0) / (vswr + 1.0), angDeg * Math.PI / 180.0);

        output.WriteLine($"VSWR {vswr} at {angDeg}°: measured Γ = {s[0, 0]}, Z = {ZOf(s[0, 0])}");

        Assert.True((expected - s[0, 0]).Magnitude < 1e-12,
            $"expected Γ = {expected}, measured {s[0, 0]}");

        // The circulation itself is untouched — this detunes the match, it does not change what the
        // component does.
        Assert.True((s[1, 0] - Complex.One).Magnitude < 1e-12, $"S21 = {s[1, 0]}");
        Assert.True(s[0, 1].Magnitude < 1e-15,                 $"S12 = {s[0, 1]}");
    }

    /// <summary>
    /// The measured VSWR is the VSWR that was typed. Stated separately from the Γ comparison above
    /// because VSWR is the number a user reads off a datasheet and types in, and a factor-of-two slip
    /// in the Γ conversion would survive a test that only compares Γ with its own formula.
    /// </summary>
    [Theory]
    [InlineData(1.25)] [InlineData(2.0)] [InlineData(3.0)]
    public void TheMeasuredVswrIsTheOneThatWasTyped(double vswr)
    {
        var s = SAt(ThreePorts() + $@"
Circulator:C1  n1 0 n2 0 n3 0  Z0=50 Ohm VSWR1={N(vswr)} Ang1=70 deg", 2.0e9);

        double g = s[0, 0].Magnitude;
        double measured = (1.0 + g) / (1.0 - g);
        Assert.True(Math.Abs(measured - vswr) < 1e-9, $"typed {vswr}, measured {measured:G8}");
    }

    /// <summary>
    /// <b>The rejected design, measured, with the exact relation it actually obeys.</b> A complex
    /// <c>Z0</c> is accepted by the block — the wave stamp takes any reference impedance — but the
    /// reflection it produces at port 1 is NOT the one a user would read off it. With the ideal
    /// permutation S and all three ports terminated in <c>Z_L</c>, a wave entering port 1 leaves at
    /// port 2, reflects, circulates to port 3, reflects again, and only then returns to port 1, so
    /// in the block's OWN reference frame:
    /// <code>
    ///   Γ₁ = conj(ρ²)      with   ρ = (Z_L − conj(Z0)) / (Z_L + Z0)
    /// </code>
    /// — the two terminations' mismatch squared, and nothing the user typed. What a 50 Ω system then
    /// MEASURES is that number carried into the 50 Ω frame, which moves it again.
    ///
    /// <para>Both halves are asserted: the closed form to 1e-12, and the fact that the measured
    /// number is nowhere near the <c>(conj(Z0) − 50)/(conj(Z0) + 50)</c> a user reaching for
    /// <c>Z0</c> would expect. This is the whole argument for <c>VSWR1</c>/<c>Ang1</c> existing at
    /// all, so it is here as arithmetic rather than as a sentence in a brief.</para>
    /// </summary>
    [Theory]
    [InlineData(25,  30)]
    [InlineData(10, -70)]
    [InlineData( 5, 100)]
    public void AComplexZ0GivesTheTwoHopReflectionAndNotTheOneItLooksLike(double r, double x)
    {
        var z0 = new Complex(r, x);

        var s = SAt(ThreePorts() + $@"
Circulator:C1  n1 0 n2 0 n3 0  Z0={N(r)}{(x < 0 ? "-" : "+")}j{N(Math.Abs(x))} Ohm", 2.0e9);

        // The measured Γ is against 50 Ω; carry it into the block's own reference frame.
        var zSeen   = ZOf(s[0, 0]);
        var gammaZ0 = (zSeen - Complex.Conjugate(z0)) / (zSeen + z0);

        var rho      = (50.0 - Complex.Conjugate(z0)) / (50.0 + z0);
        var twoHop   = Complex.Conjugate(rho * rho);
        var lookLike = (Complex.Conjugate(z0) - 50.0) / (Complex.Conjugate(z0) + 50.0);

        output.WriteLine($"Z0 = {z0}: measured Γ(50Ω) = {s[0, 0]} -> Z = {zSeen}");
        output.WriteLine($"  in the block's frame {gammaZ0}, two-hop conj(ρ²) = {twoHop}");
        output.WriteLine($"  what Z0 looks like it should give: {lookLike}");

        Assert.True((gammaZ0 - twoHop).Magnitude < 1e-12,
            $"expected conj(ρ²) = {twoHop}, got {gammaZ0}");
        Assert.True((s[0, 0] - lookLike).Magnitude > 0.15,
            $"a complex Z0 gave {s[0, 0]}, close to the {lookLike} it looks like it should - the "
          + $"argument for per-port VSWR/Ang rests on these differing, so re-derive it before "
          + $"changing it");
    }

    /// <summary>
    /// And nothing changes for a design that does not touch the new parameters: a circulator stating
    /// only <c>RL</c> measures exactly the return loss it states, at zero angle, on all three ports.
    /// </summary>
    [Fact]
    public void ACirculatorStatingOnlyRlIsUnchanged()
    {
        var s = SAt(ThreePorts() + @"
Circulator:C1  n1 0 n2 0 n3 0  Z0=50 Ohm RL=20", 2.0e9);

        for (int p = 0; p < 3; p++)
            Assert.True((s[p, p] - new Complex(0.1, 0)).Magnitude < 1e-12,
                $"port {p + 1}: {s[p, p]}");
    }
}
