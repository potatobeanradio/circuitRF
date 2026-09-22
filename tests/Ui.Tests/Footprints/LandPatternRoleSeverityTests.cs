using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// Every shipped board technology declares a courtyard layer, and the note for one that does not is
/// <b>Info</b> — reported from the field, 2026-09-21: changing a part's footprint raised a WARNING
/// about a layer circuitRF had never shipped in any technology, which is noise the user cannot act
/// on and cannot avoid.
/// </summary>
public sealed class LandPatternRoleSeverityTests
{
    /// <summary>The four board technologies <c>ShippedTechnologies</c> offers. An MMIC is not one —
    /// it is refused as a land-pattern target outright, which its own test covers.</summary>
    public static TheoryData<string> BoardTechnologies =>
    [
        "pcb-2layer_FR-4_70mil_1oz",
        "pcb-2layer_RO4350B_20mil_1oz",
        "pcb-2layer_RO4350B_30mil_1oz",
        "pcb-4layer_FR-4_62mil_1oz",
    ];

    [Theory]
    [MemberData(nameof(BoardTechnologies))]
    public void EveryShippedBoardTechnologyResolvesAllFourRolesAndSaysNothing(string name)
    {
        var tech = ShippedTechnologies.Load(name);
        var diagnostics = new List<string>();
        var roles = LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, diagnostics);

        // The whole point: placing a part on anything circuitRF ships must produce NO message at all.
        // Before this, every board technology shipped without a courtyard layer, so every part placed
        // on every one of them raised a warning — beside the silkscreen warning, which is the one
        // that matters, and which constant unavoidable noise beside it teaches people to ignore.
        Assert.NotNull(roles.Copper);
        Assert.NotNull(roles.Soldermask);
        Assert.NotNull(roles.Silkscreen);
        Assert.NotNull(roles.Assembly);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AMissingCourtyardIsInformationalAndAMissingSilkOrMaskIsNot()
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var silk = LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, []).Silkscreen!.Value;
        var court = LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, []).Assembly!.Value;

        // Asserted against the REAL produced sentences, not against a literal: IsInformational keys
        // on the layer aliases the sentence names, so rewording it and dropping them would silently
        // re-promote the courtyard note to a warning. This is what turns red instead.
        Assert.Single(Diagnose(tech, court), LandPatternLayers.IsInformational);
        Assert.DoesNotContain(Diagnose(tech, silk), LandPatternLayers.IsInformational);

        // And the two that do matter stay warnings — a pad that cannot be soldered, and a part you
        // cannot identify once the board is populated.
        Assert.Single(Diagnose(tech, silk));
        Assert.DoesNotContain("courtyard", Assert.Single(Diagnose(tech, silk)));
    }

    [Fact]
    public void ThePcbStarterAndTheShipped2LayerTechnologyDeclareTheSameRoles()
    {
        // StarterTechnologies' own comment says its aliases are declared "identically in the shipped
        // resources/technologies/pcb-*.ctech" — two definitions of one technology, and New Technology
        // hands out the in-code one. Adding the courtyard to the files and not to the starter would
        // have left a user who made their own technology seeing the note this change exists to end.
        var starter = StarterTechnologies.Pcb2Layer();
        var shipped = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");

        var diagnostics = new List<string>();
        var starterRoles = LandPatternLayers.Resolve(starter, PCellLayerSelection.Default, diagnostics);
        Assert.Empty(diagnostics);

        Assert.Equal(
            Aliases(shipped).OrderBy(a => a, System.StringComparer.Ordinal),
            Aliases(starter).OrderBy(a => a, System.StringComparer.Ordinal));
        Assert.NotNull(starterRoles.Assembly);

        static IEnumerable<string> Aliases(Technology t)
            => t.Layers.Select(l => l.Interchange?.PcbLayerName)
                       .Where(n => n is { Length: > 0 })
                       .Cast<string>();
    }

    /// <summary>What <see cref="LandPatternLayers.Resolve"/> says about <paramref name="tech"/> with
    /// one layer taken out of it.</summary>
    private static List<string> Diagnose(Technology tech, LayerKey removed)
    {
        var stripped = TechPersistence.Deserialize(TechPersistence.Serialize(tech));
        stripped.Layers.RemoveAll(l => l.Key.Equals(removed));
        var diagnostics = new List<string>();
        LandPatternLayers.Resolve(stripped, PCellLayerSelection.Default, diagnostics);
        return diagnostics;
    }
}
