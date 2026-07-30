using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-misc-termg-units-technologies.md §3 (R-misc-6/7/9): the four shipped
/// default technologies, embedded as real .ctech text assets and parsed through the normal
/// TechPersistence reader — never hand-transcribed into C#. A malformed shipped technology must
/// fail HERE, in CI, not on a new user's first run (R-misc-7's own framing).
/// </summary>
public sealed class ShippedTechnologiesTests
{
    [Fact]
    public void All_ListsExactlyTheFourShippedFiles()
    {
        var ids = ShippedTechnologies.All.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                "mmic-GaAs_2LM_100um",
                "pcb-2layer_FR-4_70mil_1oz",
                "pcb-2layer_RO4350B_20mil_1oz",
                "pcb-2layer_RO4350B_30mil_1oz",
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

    // R-misc-9: the four shipped Names must be distinguishable — no two entries read the same.
    [Fact]
    public void AllFourNames_AreDistinguishable_NoTwoReadTheSame()
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
    public void PcbTechnology_HasViaStackupEntry_WithFillModelAndSpan(string id)
    {
        var tech = ShippedTechnologies.Load(id);
        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.NotNull(via.Fill);
        Assert.False(string.IsNullOrEmpty(via.SpanFromLayer));
        Assert.False(string.IsNullOrEmpty(via.SpanToLayer));
        if (via.Fill == ViaFillKind.Plated)
            Assert.True(via.WallThicknessDbu is > 0);
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
