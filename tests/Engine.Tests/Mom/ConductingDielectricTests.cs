// SI-SIG — a dielectric's conductivity reaches the planar kernel. The accuracy gates against the committed
// Palace reference are in ConductingDielectricReferenceTests; these are the routine claims.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class ConductingDielectricTests
{
    private const double F = 5e9, Sigma = 10.0, EpsR = 11.9, TanD = 0.002;

    private static readonly EmMaterial Si = new(EpsR, TanD) { SigmaSm = Sigma };

    private static LayerStack Stack(EmMaterial silicon) =>
        new(Termination.Pec, [new MediumLayer(100e-6, silicon), new MediumLayer(2e-6, new EmMaterial(3.9))], Termination.Air);

    private static Complex Formula() =>
        new Complex(EpsR, -EpsR * TanD) - Complex.ImaginaryOne * Sigma / (2 * Math.PI * F * EmConstants.Eps0);

    /// <summary>Gate 2: ε* against the formula, at both Green's functions' own input.</summary>
    [Fact]
    public void TheConductivityReachesTheGreensFunctionAsTheFormulaSays()
    {
        var one = new SpectralGreens(new GroundedSlab(100e-6, Si), F);
        Assert.Equal(0, (one.EpsR - Formula()).Magnitude, 9);

        var layered = new LayeredSpectralGreens(Stack(Si), F);
        Assert.Equal(0, (layered.Stack.Layers[0].Material.EpsComplex - Formula()).Magnitude, 9);
    }

    /// <summary>Gate 1 by construction: nothing is rebuilt when no material states σ.</summary>
    [Fact]
    public void WithNoConductivityTheMediumIsTheSameInstance()
    {
        var stack = Stack(new EmMaterial(EpsR, TanD));
        Assert.False(stack.Conducts);
        Assert.Same(stack, stack.AtFrequency(F));
        var slab = GroundedSlab.GaAsStarter;
        Assert.Same(slab, slab.AtFrequency(F));
        Assert.Same(slab.Material, slab.Material.AtFrequency(F));
    }

    [Fact]
    public void AConductingMaterialHasNoPermittivityUntilAFrequencyIsNamed() =>
        Assert.Throws<InvalidOperationException>(() => Si.EpsComplex);

    /// <summary>The kernel's Z_c keeps C's imaginary part: a shunt loss gives it a positive phase.</summary>
    [Fact]
    public void ZcOnAConductingSubstrateHasTheInductivePhase()
    {
        var slab = new GroundedSlab(100e-6, new EmMaterial(EpsR) { SigmaSm = Sigma });
        var problem = PlanarLineFixtures.Line(slab, PlanarLineFixtures.GaAsHeroWidthM, 1e-3, F);
        var r = new PlanarKernel().Solve(problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [F]);
        Assert.True(r.Solve.Points[0].Calibrations[0].Zc.Imaginary > 0);
    }

    private static IReadOnlyList<EmFinding> Findings(double sigma, double fLo, double fHi, bool group = false) =>
        PlanarConductingMedium.Findings(Stack(new EmMaterial(EpsR) { SigmaSm = sigma }), fLo, fHi, 27e9, group);

    /// <summary>Gate 4: 0.01 Ω·cm names the range, and 10 Ω·cm, which the calibration serves, does not warn.</summary>
    [Fact]
    public void TheRangeTheCalibrationCannotServeIsAWarningNamingIt()
    {
        var low = Findings(1e4, 1e9, 40e9).Where(f => f.Severity == EmSeverity.Warning).ToList();
        Assert.Contains("1 GHz to 27 GHz", Assert.Single(low).Text);

        Assert.DoesNotContain(Findings(10, 1e9, 40e9), f => f.Severity == EmSeverity.Warning);

        // Entirely above the crossover the measured γ is in use, so there is nothing to warn about.
        Assert.DoesNotContain(Findings(1e4, 30e9, 40e9), f => f.Severity == EmSeverity.Warning);
    }

    [Fact]
    public void ARunSaysWhatTheConductivityAmountsToAtTheSweepsEnds()
    {
        string note = Findings(10, 1e9, 40e9).Single(f => f.Severity == EmSeverity.Note).Text;
        Assert.Contains("σ = 10 S/m", note);
        Assert.Contains("10 Ω·cm", note);
        Assert.Contains("tanδ 15.1 at 1 GHz", note);
        Assert.Contains("0.378 at 40 GHz", note);
    }

    [Fact]
    public void ACoupledGroupOnAConductingSubstrateWarnsAcrossTheSweep() =>
        Assert.Contains(Findings(10, 1e9, 40e9, group: true), f => f.Severity == EmSeverity.Warning && f.Text.Contains("coupled-port"));
}
