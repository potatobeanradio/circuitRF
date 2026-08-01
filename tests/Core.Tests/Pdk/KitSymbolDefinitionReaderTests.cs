using System.Linq;
using System.Text;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// Recovering what a kit calls a part's parameters. The netlist gives the formulations and which one
/// to default to, but not the kit's own NAME for that choice — and a name circuitRF invents appears
/// nowhere in the kit's documentation, so a user cannot search for it.
/// </summary>
public sealed class KitSymbolDefinitionReaderTests
{
    /// <summary>A compiled definition, as far as anything can read one: identifier and text constants
    /// in declaration order, separated by structure nothing tries to interpret.</summary>
    private static byte[] Compiled(params string[] runs)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        foreach (var run in runs)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(run));
            bytes.AddRange([0x00, 0x01, 0x00, 0x02]);   // structure between the runs
        }
        return [.. bytes];
    }

    [Fact]
    public void AParameterIsRecovered_WithTheKitsOwnNameAndDescription()
    {
        var d = KitSymbolDefinitionReader.Read(Compiled(
            "create_parm", "ModeSelect", "Build as compact or table-based",
            "PARM_NO_DISPLAY", "PARM_STRING", "UNITLESS_UNIT"));

        var p = Assert.Single(d.Parameters);
        Assert.Equal("ModeSelect", p.Name);
        Assert.Equal("Build as compact or table-based", p.Description);
        Assert.True(p.IsText);
    }

    [Fact]
    public void ParametersComeBackInDeclarationOrder_EachWithItsOwnFlags()
    {
        var d = KitSymbolDefinitionReader.Read(Compiled(
            "create_parm", "ModeSelect", "pick a formulation", "PARM_STRING",
            "create_parm", "Rth",        "thermal resistance",  "PARM_REAL"));

        Assert.Equal(["ModeSelect", "Rth"], d.Parameters.Select(p => p.Name));
        Assert.True(d.Parameters[0].IsText);
        Assert.False(d.Parameters[1].IsText);   // the flag belongs to its own parameter, not the last
    }

    [Fact]
    public void AParameterWithNoDescription_IsStillRecovered()
    {
        var d = KitSymbolDefinitionReader.Read(Compiled("create_parm", "Rth", "PARM_REAL"));

        Assert.Equal("Rth", Assert.Single(d.Parameters).Name);
        Assert.Equal("", d.Parameters[0].Description);
    }

    [Fact]
    public void TheKitsOwnVocabularyIsKept_TheLanguagesIsNot()
    {
        // The part a definition NAMES is how it is matched to that part; the API words it calls are
        // noise, and keeping them would match every definition to everything.
        var d = KitSymbolDefinitionReader.Read(Compiled(
            "set_simulator_type", "create_parm", "Mode", "PARM_STRING", "ACME_PART_A", "strcat"));

        Assert.Contains("ACME_PART_A", d.ReferencedNames);
        Assert.DoesNotContain("create_parm", d.ReferencedNames);
        Assert.DoesNotContain("PARM_STRING", d.ReferencedNames);
    }

    [Fact]
    public void AFileWithNothingRecognisable_YieldsNoParameters_AndDoesNotThrow()
        => Assert.Empty(KitSymbolDefinitionReader.Read([0x00, 0xFF, 0x7F, 0x01]).Parameters);

    [Fact]
    public void AMissingFile_IsNull_NotAnError()
        => Assert.Null(KitSymbolDefinitionReader.TryReadFile("/nowhere/at/all.bin"));
}
