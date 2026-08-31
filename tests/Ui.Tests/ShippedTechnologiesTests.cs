using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-misc-termg-units-technologies.md §3 (R-misc-6/7/9): the shipped
/// default technologies, embedded as real .ctech text assets and parsed through the normal
/// TechPersistence reader — never hand-transcribed into C#. A malformed shipped technology must
/// fail HERE, in CI, not on a new user's first run (R-misc-7's own framing).
/// </summary>
public sealed class ShippedTechnologiesTests
{
    [Fact]
    public void All_ListsExactlyTheShippedFiles()
    {
        var ids = ShippedTechnologies.All.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                "mmic-GaAs_2LM_100um",
                "pcb-2layer_FR-4_70mil_1oz",
                "pcb-2layer_RO4350B_20mil_1oz",
                "pcb-2layer_RO4350B_30mil_1oz",
                "pcb-4layer_FR-4_62mil_1oz",
            }.OrderBy(x => x, StringComparer.Ordinal),
            ids);
    }

    [Fact]
    public void DefaultId_IsAmongTheShippedEntries()
    {
        Assert.Contains(ShippedTechnologies.All, e => e.Id == ShippedTechnologies.DefaultId);
    }

    // R-misc-7: every shipped technology parses, round-trips, and passes TechValidation with zero
    // problems — the actual gate a malformed shipped file must fail.
    [Theory]
    [InlineData("mmic-GaAs_2LM_100um")]
    [InlineData("pcb-2layer_FR-4_70mil_1oz")]
    [InlineData("pcb-2layer_RO4350B_20mil_1oz")]
    [InlineData("pcb-2layer_RO4350B_30mil_1oz")]
    [InlineData("pcb-4layer_FR-4_62mil_1oz")]
    public void ShippedTechnology_Parses_RoundTrips_AndPassesValidation(string id)
    {
        var tech = ShippedTechnologies.Load(id);

        Assert.False(string.IsNullOrWhiteSpace(tech.Name));
        Assert.NotEmpty(tech.Layers);
        Assert.NotEmpty(tech.Stackup.Layers);

        var problems = TechValidation.Validate(tech);
        Assert.Empty(problems);

        // Round-trip through the SAME writer/reader a user's own .ctech goes through.
        string json = TechPersistence.Serialize(tech);
        var reloaded = TechPersistence.Deserialize(json);
        Assert.Equal(tech.Name, reloaded.Name);
        Assert.Equal(tech.Layers.Count, reloaded.Layers.Count);
        Assert.Equal(tech.Stackup.Layers.Count, reloaded.Stackup.Layers.Count);
    }

    // R-misc-9: the shipped Names must be distinguishable — no two entries read the same.
    [Fact]
    public void AllNames_AreDistinguishable_NoTwoReadTheSame()
    {
        var names = ShippedTechnologies.All.Select(e => ShippedTechnologies.Load(e).Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    // R-misc-10: each shipped technology already carries the via stackup entry (fill model + span)
    // and the MMIC file carries the two-metal stack with an air layer at εr = 1 — the prerequisite
    // check this brief's §3.3 asked for, pinned as a permanent regression guard.
    [Theory]
    [InlineData("pcb-2layer_FR-4_70mil_1oz")]
    [InlineData("pcb-2layer_RO4350B_20mil_1oz")]
    [InlineData("pcb-2layer_RO4350B_30mil_1oz")]
    [InlineData("pcb-4layer_FR-4_62mil_1oz")]
    public void PcbTechnology_HasViaStackupEntry_WithFillModelAndSpan(string id)
    {
        var tech = ShippedTechnologies.Load(id);
        var vias = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).ToList();
        Assert.NotEmpty(vias);
        foreach (var via in vias)
        {
            Assert.NotNull(via.Fill);
            Assert.False(string.IsNullOrEmpty(via.SpanFromLayer));
            Assert.False(string.IsNullOrEmpty(via.SpanToLayer));
            if (via.Fill == ViaFillKind.Plated)
                Assert.True(via.WallThicknessDbu is > 0);
        }
    }

    /// <summary>
    /// User-reported, 2026-08-30: most of their boards are 4-layer. What makes this starter usable
    /// rather than merely present is that EVERY conductor behaves sensibly in the planar EM path,
    /// which took a second pass to get right — the first cut marked only Inner 1 and left the two
    /// lower conductors in states nobody could act on (see this test's siblings in
    /// FourLayerGroundReferenceTests, which run the extractor rather than reading the file).
    ///
    /// <para><b>TWO ground references, and the pairing is the point.</b> R-em-4 resolves ground as
    /// the highest ground-designated conductor BELOW the signal level, so one plane can only serve
    /// the layers above it. Inner 1 references L1 across the 8 mil top prepreg; Bottom Copper
    /// references Inner 2 across the 8 mil bottom prepreg. Both signal layers therefore solve, and
    /// each is 8 mil off its own plane — the symmetric SIG/GND/SIG/GND build, not SIG/GND/PWR/SIG.
    /// R-pc-9's substrate resolution is unaffected: it takes the NEAREST designated ground beneath
    /// the topmost conductor, which is still Inner 1.</para>
    ///
    /// <para>Two via entries on separate drawing layers: a PTH is what a fab builds, and a stitching
    /// via to the reference plane is the one <c>BuildVias</c> can turn into an attachment basis.
    /// Sharing a drawing layer would make them indistinguishable to the Via tool.</para>
    /// </summary>
    [Fact]
    public void FourLayerPcb_PairsEachSignalLayerWithItsOwnGroundPlane_AndShipsAGroundViaBesideThePth()
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");

        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        Assert.Equal(4, conductors.Count);
        Assert.All(conductors, c => Assert.NotEmpty(c.DrawingLayers));

        // Top-to-bottom: signal, ground, signal, ground. Asserted as the whole pattern rather than
        // one membership check, because it is the ALTERNATION that makes every layer solvable.
        Assert.Equal([false, true, false, true], conductors.Select(c => c.IsGroundReference));

        var ground = conductors[1];
        var pth = Assert.Single(tech.Stackup.Layers,
            l => l.Kind == StackupKind.Via && l.SpanToLayer == conductors[3].Name);
        Assert.Equal(conductors[0].Name, pth.SpanFromLayer);

        var stitch = Assert.Single(tech.Stackup.Layers,
            l => l.Kind == StackupKind.Via && l.SpanToLayer == ground.Name);
        Assert.Equal(conductors[0].Name, stitch.SpanFromLayer);

        Assert.NotEqual(pth.DrawingLayers[0], stitch.DrawingLayers[0]);
    }

    [Fact]
    public void MmicTechnology_HasTwoMetalStack_WithAirDielectricAtEpsr1_AndMetal1Metal2ViaPost()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name).ToList();
        Assert.Contains("Metal1", conductors);
        Assert.Contains("Metal2", conductors);

        var air = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Dielectric && l.Name == "Air");
        Assert.Equal(1.0, air.Epsr, 6);

        var post = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via && l.Name.Contains("Metal1-Metal2"));
        Assert.Equal(ViaFillKind.Solid, post.Fill);
        Assert.Equal("Metal1", post.SpanFromLayer);
        Assert.Equal("Metal2", post.SpanToLayer);
    }

    /// <summary>
    /// <b>The ONE shipped MMIC technology carries the MIM module, and the module is TIED to its
    /// plate.</b> MIM-2 shipped it as a second file because a capacitor dielectric between the
    /// interconnect metals refused every airbridge run and moved a Metal1 line; MIM-7 removed that
    /// premise — <see cref="StackupLayer.PresentWithLayer"/> makes the film enter an EM run's medium
    /// only when its plate is one of that run's analysis levels — so there is one file again.
    ///
    /// <para>What is asserted here is the DATA half: the module's three stackup entries, its two
    /// drawing layers, the tie, and the arithmetic that keeps Metal2 exactly 3 µm above Metal1 (the
    /// air gap paid for both new bands, which is what makes an interconnect-only run bit-identical
    /// to the pre-module stack). The extraction half is <c>MimCapacitorTests</c>.</para>
    /// </summary>
    [Fact]
    public void MmicTechnology_CarriesTheMimModule_TiedToItsPlate()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");

        var plate = Assert.Single(tech.Stackup.Layers, l => l.Name == "MIM Metal" && l.Kind == StackupKind.Conductor);
        var thin  = Assert.Single(tech.Stackup.Layers, l => l.Name == "MIM Dielectric");
        var via   = Assert.Single(tech.Stackup.Layers, l => l.Name == "MIM Via" && l.Kind == StackupKind.Via);
        Assert.Equal(6.8, thin.Epsr, 6);
        Assert.Empty(thin.DrawingLayers);
        Assert.Equal("MIM Metal", via.SpanFromLayer);
        Assert.Equal("Metal2",    via.SpanToLayer);
        Assert.Contains(tech.Layers, l => l.Name == "MIM Metal");
        Assert.Contains(tech.Layers, l => l.Name == "MIM Via");

        // MIM-7 — the tie itself, and the fact that it is the ONLY one. A second tied dielectric
        // would deactivate on its own schedule and is not something this technology means.
        Assert.Equal("MIM Metal", thin.PresentWithLayer);
        Assert.All(tech.Stackup.Layers.Where(l => l.Name != "MIM Dielectric"),
                   l => Assert.Null(l.PresentWithLayer));

        // Metal2 still sits 3 µm above Metal1: the air gap paid for both new bands.
        var air = tech.Stackup.Layers.Single(l => l.Name == "Air");
        Assert.Equal(2550, air.ThicknessDbu);
        Assert.Equal(3000, air.ThicknessDbu + plate.ThicknessDbu + thin.ThicknessDbu);

        // MIM-6 — Metal1's EM sheet sits on the TOP of its band, so a run that DOES analyse the
        // plate reads the gap as the capacitor dielectric alone. MIM-7's extraction rule reverts it
        // for any run that does not, which is what keeps an ordinary Metal1 line on 100 µm of GaAs.
        Assert.Equal(ConductorSheetSurface.Top,
                     tech.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt);
        Assert.All(tech.Stackup.Layers.Where(l => l.Name != "Metal1"), l => Assert.Null(l.SheetAt));
    }

    [Fact]
    public void Load_UnknownId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ShippedTechnologies.Load("not-a-real-technology"));
    }

    [Fact]
    public void LoadRawJson_IsTheExactAuthoredBytes_ParsesToTheSameTechnologyAsLoad()
    {
        var entry = ShippedTechnologies.All.Single(e => e.Id == ShippedTechnologies.DefaultId);
        string raw = ShippedTechnologies.LoadRawJson(entry);
        var fromRaw = TechPersistence.Deserialize(raw);
        var fromLoad = ShippedTechnologies.Load(entry);
        Assert.Equal(fromLoad.Name, fromRaw.Name);
        Assert.Contains("\"FormatVersion\"", raw); // genuinely the raw .ctech JSON, not a re-serialization
    }
}
