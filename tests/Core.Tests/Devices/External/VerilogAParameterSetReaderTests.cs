using System;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// PM2 P2's gate: a fitted parameter set written in Verilog-A declaration syntax round-trips,
/// unknown names are REPORTED rather than dropped, case is aligned to the model's own spelling, both
/// number spellings parse, and a comment containing a semicolon does not truncate a declaration.
///
/// <para>Fixtures are written here rather than committed: no model family, version, author or
/// external path enters this repository.</para>
/// </summary>
public sealed class VerilogAParameterSetReaderTests
{
    [Fact]
    public void APlainSetRoundTrips()
    {
        var parsed = VerilogAParameterSetReader.Parse("""
            parameter real vxo = 1.3e7;
            parameter real beta = 1.8;
            parameter integer nfing = 4;
            """);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("vxo",   parsed[0].Name);
        Assert.Equal("1.3e7", parsed[0].ValueText);
        Assert.Equal("beta",  parsed[1].Name);
        Assert.Equal("1.8",   parsed[1].ValueText);
        Assert.Equal("nfing", parsed[2].Name);
        Assert.Equal("4",     parsed[2].ValueText);
    }

    [Fact]
    public void EngineeringNotationAndBareExponentBothParse()
    {
        // Both spellings are in real fitted sets, and both must survive as TEXT — the model reads
        // the value, and a round trip through a double is a chance to change a number nobody asked
        // to change.
        var parsed = VerilogAParameterSetReader.Parse("""
            parameter real cgd = 1.3e-15;
            parameter real cgs = 2.4p;
            parameter real rd  = -4;
            parameter real w   = 1.0E+3;
            """);

        Assert.Equal(["1.3e-15", "2.4p", "-4", "1.0E+3"],
                     parsed.Select(p => p.ValueText).ToArray());
    }

    [Fact]
    public void ACommentContainingASemicolonDoesNotTruncateTheDeclaration()
    {
        // The trap this test exists for: matching on a semicolon in the RAW text would end the
        // declaration inside the comment and leave `beta` unread entirely.
        var parsed = VerilogAParameterSetReader.Parse("""
            parameter real vxo = 1.3e7;  // fitted at 25 C; re-extract above 85 C
            parameter real beta = 1.8;
            """);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("1.3e7", parsed[0].ValueText);
        Assert.Equal("beta",  parsed[1].Name);
    }

    [Fact]
    public void ACommentedOutDeclarationIsNotRead()
    {
        var parsed = VerilogAParameterSetReader.Parse("""
            // parameter real vxo = 9.9e9;
            /* parameter real beta = 99; */
            parameter real gamma = 0.5;
            """);

        Assert.Single(parsed);
        Assert.Equal("gamma", parsed[0].Name);
    }

    [Fact]
    public void RangeConstraintsAreIgnoredRatherThanReadAsPartOfTheValue()
    {
        var parsed = VerilogAParameterSetReader.Parse("""
            parameter real r = 1.0 from (0:inf);
            parameter real n = 1.2 from [1:2] exclude 1.5;
            """);

        Assert.Equal("1.0", parsed[0].ValueText);
        Assert.Equal("1.2", parsed[1].ValueText);
    }

    [Fact]
    public void LocalparamAndTypelessAndStringFormsAllParse()
    {
        var parsed = VerilogAParameterSetReader.Parse("""
            localparam real k = 1.38e-23;
            parameter tref = 27.0;
            parameter string version = "2.1";
            """);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("1.38e-23", parsed[0].ValueText);
        Assert.Equal("27.0",     parsed[1].ValueText);
        // The quotes are the literal's syntax, not part of the value the model receives.
        Assert.Equal("2.1",      parsed[2].ValueText);
    }

    // ── Matching to a model ──────────────────────────────────────────────────

    [Fact]
    public void CaseIsAlignedToTheModelsOwnSpelling()
    {
        // The worker matches with strcmp, so a lower-case set reaching an upper-case model would
        // otherwise have every one of its names refused.
        var parsed = VerilogAParameterSetReader.Parse("parameter real vxo = 1.3e7;");
        var set    = VerilogAParameterSetReader.MatchToModel(parsed, ["VXO", "BETA"]);

        Assert.Single(set.Applied);
        Assert.Equal("VXO", set.Applied[0].Name);
        Assert.Empty(set.Unknown);
    }

    [Fact]
    public void AnUndeclaredNameIsReportedRatherThanDropped()
    {
        // A set written for a different version of the same family is the COMMON case, and a silent
        // drop is a wrong answer that converges: the device runs on the model's own defaults for
        // everything that went missing and looks perfectly healthy.
        var parsed = VerilogAParameterSetReader.Parse("""
            parameter real vxo = 1.3e7;
            parameter real removed_in_v2 = 4;
            """);
        var set = VerilogAParameterSetReader.MatchToModel(parsed, ["vxo"]);

        Assert.Single(set.Applied);
        Assert.Equal(["removed_in_v2"], set.Unknown);

        string outcome = VerilogAParameterSetReader.DescribeOutcome(set, "fitted.va");
        Assert.Contains("removed_in_v2", outcome, StringComparison.Ordinal);
        Assert.Contains("Not declared", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenuineTypoIsRefusedByNameRatherThanRespelledIntoSomethingAccepted()
    {
        // AlignParameterCase respells only on a case-insensitive match, so `vxoo` stays `vxoo` and
        // is reported — it is NOT quietly turned into `vxo`.
        var parsed = VerilogAParameterSetReader.Parse("parameter real vxoo = 1.3e7;");
        var set    = VerilogAParameterSetReader.MatchToModel(parsed, ["VXO"]);

        Assert.Empty(set.Applied);
        Assert.Equal(["vxoo"], set.Unknown);
    }

    [Fact]
    public void ANameAssignedTwiceTakesTheLastValueAndIsReported()
    {
        var parsed = VerilogAParameterSetReader.Parse("""
            parameter real vxo = 1.0;
            parameter real beta = 2.0;
            parameter real vxo = 3.0;
            """);
        var set = VerilogAParameterSetReader.MatchToModel(parsed, ["vxo", "beta"]);

        Assert.Equal(2, set.Applied.Count);
        Assert.Equal("vxo", set.Applied[0].Name);        // file order is preserved
        Assert.Equal("3.0", set.Applied[0].ValueText);   // last assignment wins
        Assert.Equal(["vxo"], set.Duplicates);
    }

    [Fact]
    public void AnUnknownNameAssignedTwiceIsReportedOnce()
    {
        var parsed = VerilogAParameterSetReader.Parse("""
            parameter real gone = 1;
            parameter real gone = 2;
            """);
        var set = VerilogAParameterSetReader.MatchToModel(parsed, ["vxo"]);

        Assert.Equal(["gone"], set.Unknown);
    }

    [Fact]
    public void NothingIsMaterialisedForAParameterTheSetDoesNotAssign()
    {
        // The model declares three; the set assigns one. The other two must stay ABSENT, which
        // already means "use the model's own default" — materialising them would freeze today's
        // defaults into the design and stop a recompile from changing them.
        var parsed = VerilogAParameterSetReader.Parse("parameter real vxo = 1.3e7;");
        var set    = VerilogAParameterSetReader.MatchToModel(parsed, ["vxo", "beta", "gamma"]);

        Assert.Single(set.Applied);
        Assert.DoesNotContain(set.Applied, a => a.Name is "beta" or "gamma");
    }

    [Fact]
    public void AFileThatIsNotAParameterSetYieldsNothingRatherThanNoise()
    {
        Assert.Empty(VerilogAParameterSetReader.Parse("this is not Verilog-A at all"));
        Assert.Empty(VerilogAParameterSetReader.Parse(""));
        Assert.Empty(VerilogAParameterSetReader.Parse(null));
    }

    [Fact]
    public void ADeclarationWithoutAValueIsNotAnAssignment()
    {
        // `parameter real x;` declares without assigning — there is no value to load.
        Assert.Empty(VerilogAParameterSetReader.Parse("parameter real x;"));
    }
}
