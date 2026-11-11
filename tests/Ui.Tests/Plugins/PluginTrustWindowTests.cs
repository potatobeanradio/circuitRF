using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Plugins;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-6 §4's security gate: a denial the command line records while the application has its preferences loaded
/// survives the application's next whole-file save, the Installed list shows it, and the menu's start path refuses.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class PluginTrustWindowTests : IDisposable
{
    private readonly string _state = Path.Combine(PluginTestKit.NewScratch(), "state");

    public PluginTrustWindowTests()
    {
        Directory.CreateDirectory(_state);
        AppDataRoot.RedirectTo(_state);
        AppPreferencesIo.InvalidateCache();
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        AppPreferencesIo.InvalidateCache();
    }

    [Fact]
    public void ADenialFromTheCommandLine_SurvivesTheWindowsNextSave_AndTheMenuDoesNotRunThePlugin()
    {
        var entry = PluginTestKit.Example("hello");
        PluginHost.RecordPermission(entry.Directory, true);           // allowed in the window earlier
        _ = AppPreferencesIo.Load();                                    // the window holds its copy

        Assert.True(PluginPreferences.Read(PluginHost.PreferencesPath).RecordDenial(entry.Directory));   // `plugin deny`
        AppPreferencesIo.Update(p => p.PreferredLengthUnit = "mm");    // an unrelated change, saved whole

        var file = JsonNode.Parse(File.ReadAllText(PluginHost.PreferencesPath))!;
        Assert.False((bool)file[PluginTrustStore.PreferenceKey]![PluginTrustStore.Key(entry.Directory)]!);
        Assert.Equal("mm", (string?)file["preferred_length_unit"]);

        var row = PluginsSettingsModel.Installed(new PluginCatalogSources(null, [entry.Directory]), PluginHost.Trust(), new PluginIndex([])).Single();
        Assert.Equal(PluginTrustDecision.Denied, row.Permitted);

        // What Tools ▸ Plugins consults before it starts anything.
        var refused = PluginSession.Refusal(entry, new PluginStartOptions { Trust = PluginHost.Trust(), Policy = PluginPolicy.Resolve(null, null, _ => null) });
        Assert.Equal(PluginFailureKind.NotPermitted, refused?.Kind);
    }
}
