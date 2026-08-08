using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// brief-core-length-units R-len-2 — moving <c>nm</c> and <c>cm</c> out of <c>_identityUnits</c> and
/// into <c>_scales</c> flips <see cref="Units.IsKnown"/> from false to true, and THREE <c>.cnl</c>
/// token gates read <c>IsKnown</c> rather than <c>IsRecognizedUnit</c>.
///
/// <para><b>This closes a second latent bug, and these tests are what prove it rather than assume
/// it.</b> The instance-line path (<c>R:R1 a b R=1 nm</c>) always used <c>IsRecognizedUnit</c> and
/// was never affected — the two that were are the ones nobody thinks of: a top-level VARIABLE
/// assignment (<c>SplitExprUnit</c>) and a cell PARAMETER declaration
/// (<c>ParseParameterDeclarations</c>). Both silently produced something other than a length.</para>
/// </summary>
public sealed class LengthUnitTokenTests
{
    // ── The gate that was genuinely broken: a variable assignment ────────────

    /// <summary>
    /// <c>SplitExprUnit</c> takes the trailing token as a unit only when <c>IsKnown</c> says so.
    /// With <c>nm</c> identity-only, <c>W = 5 nm</c> kept "5 nm" as the whole EXPRESSION — which the
    /// expression parser then had to make sense of. Now the unit is split off and applied.
    /// </summary>
    [Theory]
    [InlineData("nm",    5, 5e-9)]
    [InlineData("cm",    5, 5e-2)]
    [InlineData("metre", 5, 5.0)]
    [InlineData("in",    5, 5 * 2.54e-2)]
    [InlineData("mm",    5, 5e-3)]      // control: was already correct
    public void AGlobalVariableCarryingALengthUnit_ResolvesToMetres(string unit, double coeff, double expected)
    {
        var (_, tb) = new CnlReader().Read($"W = {coeff} {unit}");

        var v = tb.GlobalVariables.Single(g => g.Name == "W");
        Assert.Equal(unit, v.Unit);

        var value = new Evaluator().Eval(v.Expression, new Scope("g"), v.Unit).AsReal();
        Assert.Equal(expected, value, 15);
    }

    /// <summary>
    /// A cell's own parameter declaration reads through the same <c>IsKnown</c> gate
    /// (<c>ParseParameterDeclarations</c>). Left unconsumed, the unit is simply lost and the default
    /// silently becomes a bare number in whatever the engine's base unit happens to be.
    /// </summary>
    [Theory]
    [InlineData("nm")]
    [InlineData("cm")]
    [InlineData("metre")]
    [InlineData("inch")]
    public void ACellParameterDeclarationCarryingALengthUnit_KeepsItsUnit(string unit)
    {
        var (lib, _) = new CnlReader().Read($"""
            define Line a b
              parameters W=5 {unit}
              R:R1 a b R=50 Ohm
            end
            """);

        var p = lib.Cells.Single().Parameters.Single(x => x.Name == "W");
        Assert.Equal(unit, p.Unit);
    }

    // ── The control: the instance-line path was never affected ──────────────

    /// <summary>
    /// The instance-line gate uses <c>IsRecognizedUnit</c>, which was true for <c>nm</c>/<c>cm</c>
    /// even as identity units — so this never minted a phantom net, before or after. Pinned as the
    /// control so a future change to that gate cannot regress it unnoticed.
    /// </summary>
    [Theory]
    [InlineData("nm")]
    [InlineData("cm")]
    [InlineData("metre")]
    [InlineData("mil")]
    [InlineData("in")]
    public void ALengthUnitAfterAnInstanceParameter_IsConsumedAndNeverBecomesANet(string unit)
    {
        var (_, tb) = new CnlReader().Read($"R:R1  a  b  R=1 {unit}");
        var r = tb.Instances.Single();

        Assert.Equal(["a", "b"], r.NetBindings);
        Assert.Equal(unit, r.Overrides.Single(o => o.Name == "R").Unit);
    }

    /// <summary>A glued length unit splits the same way a glued <c>pF</c> already did.</summary>
    [Theory]
    [InlineData("5nm",   "5", "nm")]
    [InlineData("5cm",   "5", "cm")]
    [InlineData("5mil",  "5", "mil")]
    [InlineData("5metre","5", "metre")]
    public void AGluedLengthUnit_Splits(string token, string value, string unit)
    {
        var (_, tb) = new CnlReader().Read($"R:R1  a  b  R={token}");
        var o = tb.Instances.Single().Overrides.Single(x => x.Name == "R");

        Assert.Equal(value, o.Expression);
        Assert.Equal(unit, o.Unit);
    }
}
