using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for brief-sdd-weighting-parser (brief #3):
/// validates that I[p,w≥2] and H[w]=expr are parsed from CNL, stored, evaluated, and
/// cross-validated correctly.
/// </summary>
public class SddWeightingParserTests
{
    // ── Parse helper ─────────────────────────────────────────────────────────────

    private static SddModel ParseSdd(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ec = nl.Components.First(c => c.Model is SddModel);
        return (SddModel)ec.Model;
    }

    // ── Test 1: parse + WeightedTerm with constant real H[2] ─────────────────────

    /// <summary>
    /// I[1,2]=_v1 with H[2]=2 must parse, Evaluate must return a WeightedTerm with
    /// W=2, Value=[v1], Jac=[[1]], and Weight(2,ω) must equal 2+0j.
    /// </summary>
    [Fact]
    public void Parse_I1w2_And_H2Constant_ReturnsWeightedTerm()
    {
        var sdd = ParseSdd("""
            SDD:X1  a 0  I[1,2]=_v1  H[2]=2
            """);

        const double v1 = 3.5;
        var result = sdd.Evaluate(new PortVoltages([v1]));

        // Only a w=2 bucket — no current (w=0) or charge (w=1).
        Assert.DoesNotContain(result.I, x => x != 0);
        Assert.DoesNotContain(result.Q, x => x != 0);
        Assert.Single(result.Terms);

        var term = result.Terms[0];
        Assert.Equal(2, term.W);
        Assert.Equal(v1, term.Value[0], precision: 10);
        Assert.Equal(1.0, term.Jac[0, 0], precision: 10);

        // H[2]=2 → Weight returns 2+0j at any frequency.
        double omega = 2 * Math.PI * 1e9;
        Complex w2 = sdd.Weight(2, omega);
        Assert.Equal(2.0, w2.Real,      precision: 10);
        Assert.Equal(0.0, w2.Imaginary, precision: 10);
    }

    // ── Test 2: complex H[w] — H[2]=j*2*pi*freq ──────────────────────────────────

    /// <summary>
    /// H[2]=j*2*pi*freq → Weight(2, ω) = jω (within fp tolerance).
    /// Confirms the Complex Evaluator path and the freq=ω/2π binding.
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(2.4e9)]
    [InlineData(100e6)]
    public void Weight_ComplexH2_JomegaBinding_EqualsJOmega(double freqHz)
    {
        var sdd = ParseSdd("""
            SDD:X1  a 0  I[1,2]=_v1  H[2]=j*2*pi*freq
            """);

        double omega = 2 * Math.PI * freqHz;
        Complex w2 = sdd.Weight(2, omega);

        // Expected: jω
        Assert.Equal(0.0,   w2.Real,      precision: 8);
        Assert.Equal(omega, w2.Imaginary, precision: 8);
    }

    // ── Test 3: missing H[w] → factory error ────────────────────────────────────

    /// <summary>
    /// I[1,2]=_v1 without H[2] must throw a clear error during elaboration.
    /// </summary>
    [Fact]
    public void Parse_MissingH2_ThrowsClearError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseSdd("""
                SDD:X1  a 0  I[1,2]=_v1
                """));

        Assert.Contains("H[2]", ex.Message);
        Assert.Contains("not defined", ex.Message);
    }

    // ── Test 4: redefining built-in H[0] or H[1] → error ────────────────────────

    /// <summary>
    /// H[0] and H[1] are built-in (1 and jω). Declaring them in a netlist must error.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Parse_RedefineBuiltinHw_ThrowsError(int builtInW)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseSdd($"""
                SDD:X1  a 0  H[{builtInW}]=2
                """));

        Assert.Contains($"H[{builtInW}]", ex.Message);
        Assert.Contains("built-in", ex.Message);
    }

    // ── Regression: existing SDDs (no H[w]) use the fast 4-arg path ─────────────

    [Fact]
    public void Regression_ExistingSdd_NoHigherTerms()
    {
        var sdd = ParseSdd("""
            SDD:X1  a 0  I[1,0]=_v1/50
            """);

        var result = sdd.Evaluate(new PortVoltages([10.0]));
        Assert.Empty(result.Terms);
        Assert.Equal(0.2, result.I[0], precision: 10);
    }
}
