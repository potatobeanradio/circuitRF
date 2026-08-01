using System.Linq;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// `Noise=no` and `TopologyCheck=yes` are the dialect's boolean words. Left bare they reach the
/// expression engine as variable names and elaboration fails with "Unresolved name 'no'" — reported
/// from a kit.
/// </summary>
public sealed class KitDialectBooleanTests
{
    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    [InlineData("NO")]
    [InlineData("Yes")]
    public void ADialectBoolean_BecomesAStringLiteral(string token)
    {
        var r = KitNetlistReader.Read($$"""
            define PART ( a )
              R:R1  a 0  R=50 Ohm  Noise={{token}}
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal($"\"{token}\"", inst.Overrides.Single(o => o.Name == "Noise").Expression);

        // The neighbouring value is untouched: the unit rule still applies around it.
        var res = inst.Overrides.Single(o => o.Name == "R");
        Assert.Equal("50",  res.Expression);
        Assert.Equal("Ohm", res.Unit);
    }

    [Theory]
    // The list is closed on purpose. These are genuine references and quoting them would turn every
    // parameter that refers to a variable into a meaningless string.
    [InlineData("SECOND")]
    [InlineData("KIT_NEW_LG")]
    [InlineData("Gate_Periphery*1.0e3")]
    [InlineData("nothing")]
    [InlineData("yesterday")]
    public void AnOrdinaryExpression_IsLeftExactlyAsWritten(string expression)
    {
        var r = KitNetlistReader.Read($$"""
            define PART ( a )
              SUB:T1  a 0  Size={{expression}}
            end PART
            """);

        var inst = Assert.Single(Assert.Single(r.Library.Cells).Instances);
        Assert.Equal(expression, inst.Overrides.Single(o => o.Name == "Size").Expression);
    }
}
