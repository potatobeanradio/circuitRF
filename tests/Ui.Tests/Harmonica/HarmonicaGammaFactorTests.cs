// ================================================================
//  HarmonicaGammaFactorTests.cs  —  §2.6 of brief-harmonicarf-r7c-readout-units-jitter-and-gamma-metric
//
//  §2.1  γ = V₂·conj(V₁)²/|V₁|³ — the input nonlinearity factor, on the INTRINSIC gate control
//        voltage. |γ| = |V₂|/|V₁|; ∠γ = φ₂ − 2·φ₁ — NOT arg(V₂/V₁).
//  §2.3  computed three times — OperatingPoint, MXP, MXE each read their OWN DataSet.
//  §2.4  always magnitude ∠ angle; IsComplex is deliberately false.
//  §2.5  "—" when the intrinsic plane is not located, K < 2, or |V₁| = 0.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaGammaFactorTests
{
    // GammaFactor is private; invoked via reflection so the actual closed-form math is pinned rather
    // than merely re-derived independently in the test (which could agree with a wrong implementation
    // by making the same mistake twice).
    private static Complex InvokeGammaFactor(Complex v1, Complex v2)
    {
        var method = typeof(HarmonicaSolver).GetMethod("GammaFactor", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Complex)method.Invoke(null, [v1, v2])!;
    }

    private static double WrapDegrees(double deg)
        => ((deg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

    // ══ §2.1 — the definition, against a hand-computed oracle ══════════════════════════════════

    [Fact]
    public void GammaFactor_MatchesTheHandComputedOracle()
    {
        // V1 = 2∠30°, V2 = 0.5∠100° -> |γ| = 0.25, ∠γ = 100 - 60 = 40°.
        var v1 = Complex.FromPolarCoordinates(2.0, 30.0 * Math.PI / 180.0);
        var v2 = Complex.FromPolarCoordinates(0.5, 100.0 * Math.PI / 180.0);

        var gamma = InvokeGammaFactor(v1, v2);

        Assert.Equal(0.25, gamma.Magnitude, 12);
        Assert.Equal(40.0, gamma.Phase * 180.0 / Math.PI, 9);
    }

    [Fact]
    public void GammaFactor_WrapsThePhase_ToComplexPhasesOwnPrincipalRange()
    {
        // V1 = 1.5∠10°, V2 = 0.8∠350° -> raw phi2 - 2*phi1 = 350 - 20 = 330°, which wraps to -30° —
        // Complex.Phase's own principal range is (-180°, 180°], so that is what must come back, not
        // 330°. Pinned against a plain-arithmetic wrap, independent of the implementation under test.
        var v1 = Complex.FromPolarCoordinates(1.5, 10.0 * Math.PI / 180.0);
        var v2 = Complex.FromPolarCoordinates(0.8, 350.0 * Math.PI / 180.0);

        var gamma = InvokeGammaFactor(v1, v2);

        Assert.Equal(0.8 / 1.5, gamma.Magnitude, 12);
        Assert.Equal(WrapDegrees(350.0 - 2 * 10.0), gamma.Phase * 180.0 / Math.PI, 9);
    }

    // ══ §2.1 — NOT arg(V2/V1) ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GammaFactor_IsNotArgOfTheRatio_TheMistakeASimplifiedClosedFormWouldMake()
    {
        var v1 = Complex.FromPolarCoordinates(2.0, 30.0 * Math.PI / 180.0);
        var v2 = Complex.FromPolarCoordinates(0.5, 100.0 * Math.PI / 180.0);

        // arg(V2/V1) = 100 - 30 = 70°, NOT 40°. If GammaFactor were ever "simplified" to Arg(v2/v1),
        // this would start passing at 70° instead of 40° — the exact regression this test exists to
        // catch.
        double argRatio = (v2 / v1).Phase * 180.0 / Math.PI;
        Assert.Equal(70.0, argRatio, 9);

        var gamma = InvokeGammaFactor(v1, v2);
        double gammaDeg = gamma.Phase * 180.0 / Math.PI;
        Assert.NotEqual(argRatio, gammaDeg, 6);
        Assert.Equal(40.0, gammaDeg, 9);
    }

    // ══ §2.5 — cannot be computed ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GammaFactor_GuardsAZeroFundamental_AndAnyNaNInput()
    {
        var zero = Complex.Zero;
        var v2   = Complex.FromPolarCoordinates(0.5, 1.0);
        var nan  = new Complex(double.NaN, double.NaN);

        Assert.True(double.IsNaN(InvokeGammaFactor(zero, v2).Real));
        Assert.True(double.IsNaN(InvokeGammaFactor(nan, v2).Real));
        Assert.True(double.IsNaN(InvokeGammaFactor(v2, nan).Real));
    }

    [Fact]
    public void FormatGammaFactor_RendersNaNAsADash()
    {
        Assert.Equal("—", HarmonicaReadoutFormatting.FormatGammaFactor(new Complex(double.NaN, double.NaN)));
    }

    // ══ R8C §2 — the phase is noise below GammaPhaseNoiseFloor; the magnitude is still shown ═══════

    [Fact]
    public void FormatGammaFactor_BelowFloor_SuppressesThePhase_KeepsTheMagnitude()
    {
        var g = Complex.FromPolarCoordinates(5e-4, 137.0 * Math.PI / 180.0);
        Assert.Equal("0.001∠—", HarmonicaReadoutFormatting.FormatGammaFactor(g));
    }

    [Fact]
    public void FormatGammaFactor_AboveFloor_RendersTheRealAngle()
    {
        var g = Complex.FromPolarCoordinates(1.1e-3, 137.0 * Math.PI / 180.0);
        string rendered = HarmonicaReadoutFormatting.FormatGammaFactor(g);
        Assert.DoesNotContain("∠—", rendered, StringComparison.Ordinal);
        Assert.Equal(HarmonicaReadoutFormatting.FormatComplex(g, ReadoutFormat.MagnitudeAngle), rendered);
    }

    [Fact]
    public void FormatGammaFactor_AtExactlyTheFloor_RendersTheRealAngle_TheComparisonIsStrictLessThan()
    {
        var g = Complex.FromPolarCoordinates(HarmonicaReadoutFormatting.GammaPhaseNoiseFloor,
                                             137.0 * Math.PI / 180.0);
        string rendered = HarmonicaReadoutFormatting.FormatGammaFactor(g);
        Assert.DoesNotContain("∠—", rendered, StringComparison.Ordinal);
    }

    // ══ §2.3/§2.6 items 3-5 — integration through the real solver ═════════════════════════════════

    private static HarmonicaViewModel NewSolvedVm()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Null(vm.SolveError);
        return vm;
    }

    [Fact]
    public void GammaRow_ExistsInAllThreeChunks_ImmediatelyAfterPdc()
    {
        var vm = NewSolvedVm();

        foreach (var column in new[] { ReadoutColumn.OperatingPoint, ReadoutColumn.Mxp, ReadoutColumn.Mxe })
        {
            var rows = vm.Frame.Readouts.Where(r => r.Column == column).ToArray();
            int pdcIndex = Array.FindIndex(rows, r => r.Label == "Pdc");
            Assert.True(pdcIndex >= 0, $"{column}: no Pdc row found");
            Assert.True(pdcIndex + 1 < rows.Length, $"{column}: no row after Pdc");
            Assert.Equal("γ", rows[pdcIndex + 1].Label);
        }
    }

    [Fact]
    public void GammaRow_IsNeverComplex()
    {
        // §2.4 — IsComplex is deliberately false: no real/imaginary menu can put γ into a format
        // that means nothing, and it cannot collide with Zin's own saved FormatKey state.
        var vm = NewSolvedVm();
        var gammaRows = vm.Frame.Readouts.Where(r => r.Label == "γ").ToArray();
        Assert.NotEmpty(gammaRows);
        Assert.All(gammaRows, r => Assert.False(r.IsComplex));
    }

    // Reflects the same private ReadComplex HarmonicaSolver.AddGammaRow itself calls, so this
    // recomputes γ through the identical route rather than a second, parallel one.
    private static Complex InvokeReadComplex(RfCore.Data.DataSet ds, string cube, int side, int harmonic)
    {
        var method = typeof(HarmonicaSolver).GetMethod("ReadComplex", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Complex)method.Invoke(null, [ds, cube, side, harmonic])!;
    }

    [Fact]
    public void GammaRow_IsComputedThreeTimes_FromThreeDifferentDataSets()
    {
        // The default document's default grid produces distinct MXP/MXE optima (different Γ, so
        // different operating points, so different V_intr spectra) — a shared/cached answer across
        // the three chunks would show up here as bit-identical values. Compared as RAW Complex values
        // from each chunk's own DataSet (never the formatted display string): R8C §2 intentionally
        // makes two SMALL-but-different γ magnitudes render identically ("0.000∠—") once both are
        // below the phase-noise floor, which is correct display behaviour and not evidence the three
        // computations collapsed into one.
        var vm = NewSolvedVm();
        Assert.NotNull(vm.Frame.SmithPower.Optimum);
        Assert.NotNull(vm.Frame.SmithEfficiency.Optimum);

        var opDs  = vm.Frame.Published;
        var mxpDs = vm.Frame.SmithPower.Optimum!.Published;
        var mxeDs = vm.Frame.SmithEfficiency.Optimum!.Published;
        Assert.NotNull(opDs); Assert.NotNull(mxpDs); Assert.NotNull(mxeDs);

        const int gatePort = 0;   // IntrinsicPortMap.TwoPort — the shipped default document's own mapping.
        Complex OpGamma(RfCore.Data.DataSet ds)
        {
            var v1 = InvokeReadComplex(ds, "V_intr", gatePort, 1);
            var v2 = InvokeReadComplex(ds, "V_intr", gatePort, 2);
            return InvokeGammaFactor(v1, v2);
        }

        var opGamma  = OpGamma(opDs!);
        var mxpGamma = OpGamma(mxpDs!);
        var mxeGamma = OpGamma(mxeDs!);

        Assert.False(double.IsNaN(opGamma.Real) || double.IsNaN(mxpGamma.Real) || double.IsNaN(mxeGamma.Real),
            $"expected all three to be computed on this fixture: OP={opGamma} MXP={mxpGamma} MXE={mxeGamma}");

        bool anyDiffer = opGamma != mxpGamma || opGamma != mxeGamma || mxpGamma != mxeGamma;
        Assert.True(anyDiffer, $"all three γ values were bit-identical: {opGamma}");
    }

    [Fact]
    public void GammaRow_ReadsDashAtK1_NoSecondHarmonicSolved()
    {
        // The default document's own marker set needs bands 1..3, so K is lowered AFTER construction
        // (ApplyInput's own documented behaviour: "marker bands above the new K are dropped") rather
        // than building a K=1 model directly, which the default marker set would reject outright.
        var vm = new HarmonicaViewModel();
        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyHarmonicCount, "1"));
        Assert.Equal(1, vm.Model.Settings.HarmonicCount);

        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Null(vm.SolveError);

        var opGamma = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.OperatingPoint && r.Label == "γ");
        Assert.Equal("—", opGamma.Value);
        Assert.Contains("K = 1", opGamma.Tooltip, StringComparison.Ordinal);
    }
}
