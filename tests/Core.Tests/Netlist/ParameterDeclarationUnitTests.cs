using System.Linq;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// A cell's <c>parameters</c> line reads a trailing word the way an instance line and a variable assignment
/// do (<c>Units.IsRecognizedUnit</c>). It used to test <c>Units.IsKnown</c>, which leaves out the scale-1 base
/// symbols and the measurement units, so <c>parameters Vds=3 V</c> silently lost its <c>V</c>.
/// </summary>
public sealed class ParameterDeclarationUnitTests
{
    [Theory]
    [InlineData("V")]
    [InlineData("A")]
    [InlineData("W")]
    [InlineData("dBm")]
    [InlineData("%")]
    public void AnIdentityUnit_IsKeptOnTheDeclaration(string unit)
    {
        var (lib, tb) = new CnlReader().Read($"define X (a)\n  parameters P=3 {unit}\nend\n");

        Assert.Equal(unit, lib.Cells.Single().Parameters.Single().Unit);
        Assert.Empty(tb.ReadWarnings);
    }

    [Fact]
    public void ACommentEndsTheLine_AndSpacesAroundEqualsAreAllowed()
    {
        var (lib, tb) = new CnlReader().Read("define X (a)\n  parameters L = 5 nH Vds=3 V ; Y=1 stray words\nend\n");

        var p = lib.Cells.Single().Parameters;
        Assert.Equal(["L", "Vds"], p.Select(x => x.Name));
        Assert.Equal(["nH", "V"], p.Select(x => x.Unit));
        Assert.Empty(tb.ReadWarnings);
    }

    [Fact]
    public void AWordThatIsNotAUnit_IsSkippedAndSaid()
    {
        var (lib, tb) = new CnlReader().Read("define X (a)\n  parameters W=5 mils\nend\n");

        Assert.Null(lib.Cells.Single().Parameters.Single().Unit);
        Assert.Contains("'mils'", Assert.Single(tb.ReadWarnings));
    }
}
