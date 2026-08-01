using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// A bare word after a parameter is a UNIT, never a net — and what happened when it was not one the
/// units table carried.
///
/// <para><b>Measured.</b> It ties every unused package pin to ground through
/// <c>R=1 TOhm</c>. <c>TOhm</c> was missing from the table, so it was added to the NET list instead:
/// fourteen resistors all wired to one node named "TOhm", that node constrained by nothing, and the
/// MNA matrix singular with an all-zero row. Nothing in the report mentioned a unit — the reader
/// parsed it happily and produced a different circuit.</para>
///
/// <para>So the table gained the entry, and the parser stopped guessing: once parameters have begun,
/// nets are finished by definition, and a word that is not a unit is reported rather than silently
/// becoming a node.</para>
/// </summary>
public sealed class BareTokenAfterParameterTests
{
    private static Instance Single(string line)
    {
        var (_, tb) = new CnlReader().Read(line);
        return tb.Instances.Single();
    }

    [Theory]
    [InlineData("TOhm", 1e12)]
    [InlineData("GOhm", 1e9)]
    [InlineData("MOhm", 1e6)]
    [InlineData("kOhm", 1e3)]
    [InlineData("Ohm",  1.0)]
    [InlineData("mOhm", 1e-3)]
    public void TheResistancePrefixesAreAllCarried(string unit, double scale)
    {
        // The series had a hole at each end. One of them was load-bearing on a kit; the other
        // is the same omission and would have failed the same silent way.
        Assert.Equal(scale, Units.Scale(unit));
        Assert.True(Units.IsRecognizedUnit(unit));
    }

    [Fact]
    public void AUnitAfterAParameter_IsConsumedAndNeverBecomesANet()
    {
        var r = Single("R:R1  pin23  0  R=1 TOhm  Noise=\"no\"");

        Assert.Equal(["pin23", "0"], r.NetBindings);
        Assert.Equal("TOhm", r.Overrides.Single(o => o.Name == "R").Unit);
    }

    [Fact]
    public void AWordAfterAParameterThatIsNotAUnit_IsReportedRatherThanMadeIntoANode()
    {
        // The old behaviour produced a working parse of a different circuit. An error naming the
        // token is what makes the one-entry fix obvious; a phantom node names nothing.
        var ex = Assert.Throws<CnlReadException>(() => Single("R:R1  a  b  R=1 POhm"));

        Assert.Contains("POhm", ex.Message);
        Assert.Contains("unit", ex.Message);
    }

    [Fact]
    public void ATrailingCommentOnAnInstanceLine_IsNotCircuitData()
    {
        // Never stripped here — only for variable assignments — so every word of a comment joined
        // the net list. It went unnoticed because a two-terminal model reads the first two nets and
        // ignores the rest.
        var c = Single("C:C1  Vout  Vout2  C=1m  ; near-short at 2 GHz");

        Assert.Equal(["Vout", "Vout2"], c.NetBindings);
    }

    [Fact]
    public void ASemicolonInsideAQuotedValue_IsNotACommentMarker()
    {
        // Stripping is quote-aware because a file path may perfectly well contain one, and cutting
        // the line there would truncate the path rather than a comment.
        var s = Single("""SnP:S1  a  0  File="/tmp/od;d/x.s2p"  Type="touchstone"  NumPorts=1""");

        Assert.Equal("\"/tmp/od;d/x.s2p\"", s.Overrides.Single(o => o.Name == "File").Expression);
    }

    [Fact]
    public void NetsStillComeFirstAndAreUnaffected()
    {
        var r = Single("R:R1  in  out  R=50 Ohm");

        Assert.Equal(["in", "out"], r.NetBindings);
    }
}
