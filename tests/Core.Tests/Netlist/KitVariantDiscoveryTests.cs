using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Working out, from a kit's netlist alone, which subcircuits are formulations of one part and which
/// of them circuitRF can build. Both facts are in the file; declaring either means someone writes
/// something and puts it somewhere, which is what importing a read-only kit must not require.
/// </summary>
public sealed class KitVariantDiscoveryTests
{
    private static (CircuitRF.Core.Design.Library Library, IReadOnlySet<string> Incomplete) Read(string text)
    {
        var r = KitNetlistReader.Read(text);
        return (r.Library, r.IncompleteCells);
    }

    [Fact]
    public void NamesSharingAStem_AreOnePartsFormulations()
    {
        var (lib, inc) = Read("""
            define PART_A ( p1 p2 )
              R:R1 p1 p2 R=1
            end PART_A
            define PART_B ( p1 p2 )
              R:R1 p1 p2 R=2
            end PART_B
            """);

        var family = Assert.Single(KitVariantDiscovery.Find(lib, inc));
        Assert.Equal("PART", family.Stem);
        Assert.Equal(["A", "B"], family.Choices);
        Assert.Equal("PART_A", family.CellNameFor("A"));
    }

    [Fact]
    public void AFormulationWithNoSibling_IsNotAChoice()
    {
        // One formulation is not a choice, and a picker with a single entry is noise.
        var (lib, inc) = Read("define ONLY_ONE ( p )\n  R:R1 p 0 R=1\nend ONLY_ONE");

        Assert.Empty(KitVariantDiscovery.Find(lib, inc));
    }

    [Fact]
    public void AFormulationWhoseDefinitionCouldNotBeRead_IsNotBuildable()
    {
        // The honest signal. Not that a type is unfamiliar — that is very often a device a provider
        // supplies — but that a line of the definition itself was skipped, so what is left is not the
        // circuit the kit wrote.
        var (lib, inc) = Read("""
            define PART_GOOD ( p )
              R:R1 p 0 R=1
            end PART_GOOD
            define PART_BAD ( p )
              %%% something the reader cannot take
              R:R1 p 0 R=1
            end PART_BAD
            """);

        var family = Assert.Single(KitVariantDiscovery.Find(lib, inc));
        Assert.Equal(["GOOD"], family.Buildable);
        Assert.Equal(["BAD"],  family.Unsupported);
    }

    [Fact]
    public void AnUnfamiliarType_DoesNotMakeAFormulationUnbuildable()
    {
        // Getting this backwards inverts the answer on a kit: the formulation a kit expects you
        // to use is typically the one resting on a provider's device.
        var (lib, inc) = Read("""
            define PART_X ( p )
              SOME_EXTERNAL_DEVICE:D1 p 0 Size=3
            end PART_X
            define PART_Y ( p )
              %%% unreadable
              R:R1 p 0 R=1
            end PART_Y
            """);

        Assert.Equal(["X"], Assert.Single(KitVariantDiscovery.Find(lib, inc)).Buildable);
    }

    [Fact]
    public void UnbuildabilityIsInherited_FromWhateverAFormulationInstantiates()
    {
        // A cell reads cleanly while the cell it instantiates does not; it is the whole chain.
        var (lib, inc) = Read("""
            define HELPER ( p )
              %%% unreadable
              R:R1 p 0 R=1
            end HELPER
            define PART_USES ( p )
              HELPER:H1 p
            end PART_USES
            define PART_CLEAN ( p )
              R:R1 p 0 R=1
            end PART_CLEAN
            """);

        var family = Assert.Single(KitVariantDiscovery.Find(lib, inc), f => f.Stem == "PART");
        Assert.Equal(["CLEAN"], family.Buildable);
    }

    [Fact]
    public void APartFindsItsFamily_ThroughSharedWords_NotAPrefix()
    {
        // A kit names a part and its formulations from the same words, but rarely identically.
        var (lib, inc) = Read("""
            define ACME_PA_Rev2_SPmodel_MET ( p )
              R:R1 p 0 R=1
            end ACME_PA_Rev2_SPmodel_MET
            define ACME_PA_Rev2_SPmodel_ROOT ( p )
              R:R1 p 0 R=1
            end ACME_PA_Rev2_SPmodel_ROOT
            """);

        var families = KitVariantDiscovery.Find(lib, inc);

        Assert.Equal("ACME_PA_Rev2_SPmodel",
                     KitVariantDiscovery.ForPart("ACME_PA_Rev2_MODEL", families)!.Stem);
        Assert.Null(KitVariantDiscovery.ForPart("SOMETHING_ELSE", families));
    }

    [Fact]
    public void TwoFamiliesFittingEqually_IdentifyNothing()
    {
        // Guessing would attach a formulation choice to the wrong part.
        var (lib, inc) = Read("""
            define ACME_PA_ONE ( p )
              R:R1 p 0 R=1
            end ACME_PA_ONE
            define ACME_PA_TWO ( p )
              R:R1 p 0 R=1
            end ACME_PA_TWO
            define ACME_PB_ONE ( p )
              R:R1 p 0 R=1
            end ACME_PB_ONE
            define ACME_PB_TWO ( p )
              R:R1 p 0 R=1
            end ACME_PB_TWO
            """);

        Assert.Null(KitVariantDiscovery.ForPart("ACME_PART", KitVariantDiscovery.Find(lib, inc)));
    }
}
