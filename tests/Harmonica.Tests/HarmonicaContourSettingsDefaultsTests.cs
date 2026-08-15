// ================================================================
//  HarmonicaContourSettingsDefaultsTests.cs — R8A §5/§6's own defaults gate
//
//  CircuitModel's default contour-surface settings became ContourSmooth = 0.1 / ContourEpsilon = 0.5
//  (owner-set, R8A §5). The only way to catch the ContourEpsilon half — CharmIo:334's
//  `?? defaults.ContourEpsilon` — is a round trip through a document that never wrote the field.
// ================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using CircuitRF.Harmonica;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

public sealed class HarmonicaContourSettingsDefaultsTests
{
    private static CircuitModel MinimalModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "0.1*tanh(_v2)",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.0, Vds = 28 },
        Settings = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = 2e9 },
    };

    [Fact]
    public void CircuitModel_DefaultSettings_GiveContourSmooth01AndContourEpsilon05()
    {
        var settings = new HarmonicaSettings();
        Assert.Equal(0.1, settings.ContourSmooth);
        Assert.Equal(0.5, settings.ContourEpsilon);
    }

    [Fact]
    public void CharmRoundTrip_NeverWroteEitherContourField_ComesBackWithBothNewDefaults()
    {
        string json = CharmIo.Write(MinimalModel());

        // Simulate a .charm that predates these two fields: remove them from the Settings block
        // entirely (an OMITTED field, not merely a written JSON null — CharmIo:334's own fix is
        // exactly that a file with no field at all must land on the default the same way a file that
        // explicitly persisted null does).
        var root = JsonNode.Parse(json)!.AsObject();
        var settingsNode = root["Settings"]!.AsObject();
        Assert.True(settingsNode.ContainsKey("ContourSmooth"), "fixture assumption: Write emits ContourSmooth");
        settingsNode.Remove("ContourSmooth");
        settingsNode.Remove("ContourEpsilon");

        var back = CharmIo.Read(root.ToJsonString(), baseDirectory: null, out var unresolved);
        Assert.Empty(unresolved);
        Assert.Equal(0.1, back.Settings.ContourSmooth);
        Assert.Equal(0.5, back.Settings.ContourEpsilon);
    }

    [Fact]
    public void CharmRoundTrip_AnExplicitNullEpsilon_StillComesBackAsTheDefault()
    {
        // R8A §5 — ContourEpsilon is no longer null-means-auto BY DEFAULT: a document that wrote an
        // explicit null (e.g. a user who cleared the Advanced tab's epsilon box, then saved) is
        // indistinguishable on disk from one that never wrote the field at all, and both land on 0.5
        // — the SAME behaviour every neighbouring absent-field default already has. Rbf2D's own auto
        // epsilon stays reachable within a session (clearing the box without saving/reloading), just
        // not as something that survives a round trip.
        string json = CharmIo.Write(MinimalModel());
        var root = JsonNode.Parse(json)!.AsObject();
        var settingsNode = root["Settings"]!.AsObject();
        settingsNode["ContourEpsilon"] = null;

        var back = CharmIo.Read(root.ToJsonString(), baseDirectory: null, out _);
        Assert.Equal(0.5, back.Settings.ContourEpsilon);
    }
}
