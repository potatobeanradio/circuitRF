using CircuitRF.WBond.Mom;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// The plastic <b>overmold</b> — <see cref="WBondDesign.OvermoldEr"/>, the relative permittivity of
/// the medium the wires are encapsulated in (wbond.md §3.7).
///
/// <h3>What is actually being asserted, and why it is an EXACT claim rather than a tolerance</h3>
/// <para>Both wirebond kernels are quasi-static and the encapsulant is non-magnetic, so ε_r enters in
/// exactly one place: <b>P</b>, the coefficient-of-potential matrix, is divided by it. Every
/// capacitance is therefore <b>ε_r × the air value to within rounding</b>, and every inductance is
/// <b>bit-identical</b>. Both halves are tested, because a plausible way to get this wrong is to scale
/// something downstream of the reduction — which would move the capacitance by roughly the right
/// factor and quietly move the inductance too.</para>
///
/// <para>The <b>bit-identical</b> claims are the load-bearing ones. "ε_r = 1 changes nothing" is what
/// makes this safe to ship to designs that already exist, and it is a bit-identity, not a tolerance:
/// dividing by a literal 1.0 is exact in IEEE arithmetic, so the air path must produce the same doubles
/// it produced before this existed.</para>
/// </summary>
public class OvermoldTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public OvermoldTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static WBondDesign Molded(double er)
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 6, arrayCount: 3, pointsPerWire: 7);
        design.OvermoldEr = er;
        return design;
    }

    /// <summary>
    /// Asserts <c>actual == expected</c> to a RELATIVE tolerance.
    ///
    /// <para><b>xUnit's <c>Assert.Equal(a, b, precision)</c> is decimal PLACES — an absolute
    /// tolerance</b> — and these quantities span 1e-15 F to 1e12 F⁻¹, so it is either vacuous or
    /// impossible depending on which one is being compared. Every scaling claim here is a relative
    /// one, so it is written as one.</para>
    /// </summary>
    private static void AssertRelative(double expected, double actual, double tolerance = 1e-12)
    {
        double scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
        if (scale == 0.0) return;

        double error = Math.Abs(expected - actual) / scale;
        Assert.True(error <= tolerance,
            $"Expected {expected:E17}, got {actual:E17} — relative difference {error:E3} exceeds {tolerance:E3}.");
    }

    private static CapacitanceReduction Reduce(WBondDesign design) =>
        CapacitanceReduction.Create(WireMesh.Build(design), parallel: false)
        ?? throw new InvalidOperationException("The design has no ground plane, so it has no capacitance.");

    // ---------------------------------------------------------------- the lumped model

    /// <summary>
    /// <b>Every capacitance scales by exactly ε_r.</b> The array-basis Maxwell matrix, the per-wire
    /// ground capacitance and all three numbers of the end split, at three permittivities.
    ///
    /// <para>The tolerance is 1e-12 relative and is a rounding budget, not a physics one — the two
    /// runs differ only by a division applied inside the fill instead of outside it.</para>
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(3.8)]
    [InlineData(9.9)]
    public void Capacitance_ScalesByExactlyEr(double er)
    {
        var air = Reduce(Molded(1.0));
        var mold = Reduce(Molded(er));

        for (int k = 0; k < air.ArrayCount; k++)
        {
            for (int j = 0; j < air.ArrayCount; j++)
                AssertRelative(er * air.Maxwell(k, j), mold.Maxwell(k, j));

            AssertRelative(er * air.InputSelfCapacitance(k), mold.InputSelfCapacitance(k));
            AssertRelative(er * air.OutputSelfCapacitance(k), mold.OutputSelfCapacitance(k));
            AssertRelative(er * air.EndToEndCapacitance(k), mold.EndToEndCapacitance(k));
        }

        for (int w = 0; w < air.WireCount; w++)
            AssertRelative(er * air.WireGroundCapacitance(w), mold.WireGroundCapacitance(w));

        _out.WriteLine($"er = {er}: C_arr[0,0] {air.Maxwell(0, 0) * 1e15:F4} fF -> " +
                       $"{mold.Maxwell(0, 0) * 1e15:F4} fF");
    }

    /// <summary>
    /// <b>The inductance is bit-identical.</b> An encapsulant is non-magnetic, so a permittivity that
    /// moved <b>L</b> would be a bug in the direction nobody would look — the array inductance would
    /// simply read differently and there is no closed form on screen to catch it.
    /// </summary>
    [Fact]
    public void Inductance_IsUntouchedByThePermittivity()
    {
        var air = ImpedanceReduction.Create(Molded(1.0), parallel: false).InductanceOnlyReduction();
        var mold = ImpedanceReduction.Create(Molded(4.2), parallel: false).InductanceOnlyReduction();

        for (int k = 0; k < air.ArrayCount; k++)
            for (int j = 0; j < air.ArrayCount; j++)
                Assert.Equal(air.PicoHenries(k, j), mold.PicoHenries(k, j));
    }

    /// <summary>
    /// <b>ε_r = 1 is the answer this repository already had, to the last bit.</b>
    ///
    /// <para>Compared against a fill that never sees a permittivity at all — the explicit override on
    /// <see cref="PotentialCoefficients.Fill"/> is bypassed by asking for the same geometry through a
    /// design that is, itself, at the default. Both must produce identical doubles, or "adding this
    /// changed nothing for existing designs" is a claim with a tolerance hidden in it.</para>
    /// </summary>
    [Fact]
    public void Air_ReproducesThePriorAnswerBitForBit()
    {
        var mesh = WireMesh.Build(Molded(1.0));

        var withDefault = PotentialCoefficients.Fill(mesh, parallel: false);
        var withExplicitOne = PotentialCoefficients.Fill(mesh, parallel: false,
                                                        relativePermittivity: 1.0);

        for (int i = 0; i < withDefault.Values.Length; i++)
            Assert.Equal(withDefault.Values[i], withExplicitOne.Values[i]);
    }

    /// <summary>
    /// <b>The self-resonance falls as 1/√ε_r.</b>
    ///
    /// <para>An independent tell, and the reason it is worth its own test: it is a consequence of L
    /// being untouched AND C scaling, so it fails if either half is wrong — including the failure mode
    /// where both are scaled and the ratio is preserved.</para>
    /// </summary>
    [Fact]
    public void SelfResonance_FallsAsOneOverRootEr()
    {
        const double er = 4.0;

        double Srf(double permittivity)
        {
            var design = Molded(permittivity);
            var mesh = WireMesh.Build(design);
            var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh, parallel: false), mesh);
            var cap = Reduce(design);
            return CapacitanceReduction.SelfResonanceHz(reduction, cap.TerminalShuntMatrix());
        }

        double air = Srf(1.0), mold = Srf(er);
        _out.WriteLine($"SRF {air * 1e-9:F2} GHz -> {mold * 1e-9:F2} GHz, ratio {air / mold:F4} " +
                       $"(sqrt({er}) = {Math.Sqrt(er):F4})");

        AssertRelative(Math.Sqrt(er), air / mold, 1e-9);
    }

    // ---------------------------------------------------------------- the distributed model

    /// <summary>
    /// <b>The MoM node-basis <c>P</c> scales the same way</b>, entry for entry — the distributed model
    /// is not a second implementation of the medium, it is the same one applied to different cells.
    /// </summary>
    [Fact]
    public void MomPotential_ScalesByExactlyEr()
    {
        const double er = 3.5;
        var settings = WireMomSettings.Default with { TargetSegmentsPerWire = 6, Parallel = false };

        var airMesh = WireMomMesh.Build(Molded(1.0), settings);
        var moldMesh = WireMomMesh.Build(Molded(er), settings);

        var air = NodePotential.Fill(airMesh);
        var mold = NodePotential.Fill(moldMesh);

        Assert.Equal(air.Length, mold.Length);
        for (int i = 0; i < air.Length; i++)
            AssertRelative(air[i] / er, mold[i]);
    }

    /// <summary>
    /// <b>The medium does not change how well the two bases AGREE.</b>
    ///
    /// <para>The node basis and the wire basis are gated against each other already; they do not agree
    /// exactly, because the MoM mesh re-segments each wire and the two discretisations are genuinely
    /// different — <b>6.5e-3 on this fixture, and that is a discretisation difference, not this
    /// feature's</b>. So the claim worth making here is not "they agree in a medium" (they agree
    /// exactly as well and exactly as badly as they do in air) but that the disagreement is
    /// <i>unchanged</i>: applying ε_r in two separate files is precisely the change that scales one
    /// basis and not the other, and that would move this number.</para>
    /// </summary>
    [Fact]
    public void TheMedium_DoesNotChangeHowTheTwoBasesAgree()
    {
        double Disagreement(double er)
        {
            var design = Molded(er);
            var settings = WireMomSettings.Default with { TargetSegmentsPerWire = 8, Parallel = false };

            var momMesh = WireMomMesh.Build(design, settings);
            var p = NodePotential.Fill(momMesh, farThresholdFactor: double.PositiveInfinity);
            var b = NodePotential.WireReduction(momMesh);

            int nn = momMesh.NodeCount, w = momMesh.WireCount;
            var wireBasis = PotentialCoefficients.Fill(
                WireMesh.Build(design), parallel: false, farThresholdFactor: double.PositiveInfinity);

            double worst = 0.0;
            for (int i = 0; i < w; i++)
                for (int j = 0; j < w; j++)
                {
                    double acc = 0.0;
                    for (int m = 0; m < nn; m++)
                        for (int n = 0; n < nn; n++)
                            acc += b[m * w + i] * p[m * nn + n] * b[n * w + j];

                    worst = Math.Max(worst, Math.Abs(acc - wireBasis[i, j]) / Math.Abs(wireBasis[i, j]));
                }

            return worst;
        }

        double air = Disagreement(1.0), mold = Disagreement(3.8);
        _out.WriteLine($"basis disagreement: air {air:E3}, er = 3.8 {mold:E3}");

        AssertRelative(air, mold, 1e-10);
    }

    // ---------------------------------------------------------------- refusals and persistence

    /// <summary>
    /// <b>Below 1 is refused, by name, at validation.</b> Clamping would let a design report a
    /// capacitance it did not ask for and say nothing; zero or negative would divide <b>P</b> into an
    /// infinity or a sign inversion and surface as a Cholesky breakdown far from its cause.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    public void APermittivityBelowOne_IsRefusedByName(double er)
    {
        var design = Molded(1.0);
        design.OvermoldEr = er;

        var ex = Assert.Throws<InvalidOperationException>(design.Validate);
        Assert.Contains("permittivity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at least 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>It round-trips through the <c>.wBond</c> file, and an OLD file loads as air.</b>
    ///
    /// <para>The second half is the compatibility claim: the field is additive and nullable, so a file
    /// written before overmold existed must not acquire a dielectric it never asked for. It is
    /// asserted against a real serialised document with the key removed, not against a default on the
    /// object.</para>
    /// </summary>
    [Fact]
    public void ItRoundTrips_AndAFileWithoutIt_LoadsAsAir()
    {
        string json = WBondIo.Write(Molded(3.4));
        Assert.Equal(3.4, WBondIo.Read(json).OvermoldEr);

        // The same document with the key taken out — what a pre-overmold .wBond looks like.
        string stripped = System.Text.RegularExpressions.Regex.Replace(
            json, "\"overmoldEr\"\\s*:\\s*[0-9.]+\\s*,?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.DoesNotContain("overmoldEr", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1.0, WBondIo.Read(stripped).OvermoldEr);
    }

    /// <summary>
    /// <b><see cref="WBondDesign.WithOvermoldEr"/> does not touch the design it came from.</b>
    ///
    /// <para>That is the whole reason it exists — the Touchstone export offers ε_r per file, and an
    /// export is a read. If this ever started mutating, the symptom would be a schematic quietly
    /// simulating whatever the last export happened to be set to.</para>
    /// </summary>
    [Fact]
    public void WithOvermoldEr_LeavesTheOriginalAlone()
    {
        var design = Molded(1.0);
        var view = design.WithOvermoldEr(4.5);

        Assert.Equal(1.0, design.OvermoldEr);
        Assert.Equal(4.5, view.OvermoldEr);

        // Shallow on purpose: the wires are shared, so a 600-wire design costs one object.
        Assert.Same(design.Arrays, view.Arrays);
        Assert.Equal(design.IncludeCapacitance, view.IncludeCapacitance);
        Assert.Equal(design.ReadoutFrequencyGHz, view.ReadoutFrequencyGHz);
        Assert.Same(design.GroundPlane, view.GroundPlane);

        // And the capacitance it produces is the molded one, so the shallow copy is a real medium
        // change rather than a field nothing reads.
        AssertRelative(4.5 * Reduce(design).Maxwell(0, 0), Reduce(view).Maxwell(0, 0));
    }

    /// <summary>
    /// <b>The panel readout carries it</b>, so the number on screen and the medium it was computed in
    /// come from one object rather than two.
    /// </summary>
    [Fact]
    public void ThePanelReadout_ReportsTheMedium()
    {
        var design = Molded(3.9);
        var mesh = WireMesh.Build(design);
        var readout = PanelReadout.Build(
            design, mesh,
            ArrayReduction.Reduce(InductanceMatrix.Fill(mesh, parallel: false), mesh),
            Reduce(design));

        Assert.Equal(3.9, readout.OvermoldEr);
    }
}
