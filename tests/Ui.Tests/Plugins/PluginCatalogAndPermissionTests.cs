using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Plugins;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>brief-plg-1 gates 2 and 4: listing runs nothing, and nothing runs without permission.</summary>
public class PluginCatalogAndPermissionTests
{
    private static void WriteManifest(string dir, string id, string version)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "apiVersion": 1, "id": "{{id}}", "name": "{{id}}", "version": "{{version}}",
              "run": { "python": { "entry": "p.py" } }, "kind": "action" }
            """);
        // A script that would leave a mark if anything ran it.
        File.WriteAllText(Path.Combine(dir, "p.py"), "open('RAN', 'w').write('x')\n");
    }

    [Fact]
    public void DiscoveryReadsManifestsAndStartsNothing()
    {
        string root = PluginTestKit.NewScratch();
        WriteManifest(Path.Combine(root, "a.one", "1.0.0"), "a.one", "1.0.0");
        WriteManifest(Path.Combine(root, "a.two", "1.0.0"), "a.two", "1.0.0");
        string linked = Path.Combine(root, "..", Path.GetFileName(root) + "-linked");
        WriteManifest(linked, "a.three", "0.1.0");

        var starts = PluginSession.CountStartsInThisFlow();
        var found = PluginCatalog.Discover(new PluginCatalogSources(root, EnvironmentPath: linked));

        Assert.Equal(3, found.Count);
        Assert.Equal(0, starts.Value);
        Assert.DoesNotContain(Directory.EnumerateFiles(root, "RAN", SearchOption.AllDirectories), _ => true);
        Assert.False(File.Exists(Path.Combine(linked, "RAN")));
    }

    [Fact]
    public void TheRecordedVersionIsActiveElseTheHighest()
    {
        string root = PluginTestKit.NewScratch();
        foreach (string v in new[] { "0.9.1", "0.10.0", "0.10.0-beta.1" })
            WriteManifest(Path.Combine(root, "a.one", v), "a.one", v);

        Assert.Equal("0.10.0", PluginCatalog.Discover(new PluginCatalogSources(root)).Single().Manifest!.Version);
        var pinned = new Dictionary<string, string> { ["a.one"] = "0.9.1" };
        Assert.Equal("0.9.1", PluginCatalog.Discover(new PluginCatalogSources(root, ActiveVersions: pinned)).Single().Manifest!.Version);
    }

    [Theory]
    [InlineData(PluginTrustDecision.Unknown)]
    [InlineData(PluginTrustDecision.Denied)]
    public void NoProcessStartsForAPluginNotAllowed(PluginTrustDecision decision)
    {
        var entry = PluginTestKit.Example("hello");
        var trust = new PluginTrustStore(decision == PluginTrustDecision.Denied ? [new KeyValuePair<string, bool>(entry.Directory, false)] : null);
        var starts = PluginSession.CountStartsInThisFlow();

        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, PluginTestKit.NewScratch()) with { Trust = trust });

        Assert.Equal(PluginFailureKind.NotPermitted, started.Failure!.Kind);
        Assert.Equal(0, starts.Value);
    }

    [Fact]
    public void ThePolicyFileBeatsTheEnvironmentWhichBeatsThePreference()
    {
        string install = PluginTestKit.NewScratch();
        static string? Env(string k) => k == PluginPolicy.EnvironmentVariable ? "1" : null;

        Assert.True(PluginPolicy.Resolve(install, true, _ => null).Allowed);
        Assert.False(PluginPolicy.Resolve(install, false, _ => null).Allowed);
        Assert.Equal(PluginPolicyOverride.Environment, PluginPolicy.Resolve(install, true, Env).Override);

        File.WriteAllText(Path.Combine(install, PluginPolicy.PolicyFileName), "");
        Assert.Equal(PluginPolicyOverride.PolicyFile, PluginPolicy.Resolve(install, true, Env).Override);

        var entry = PluginTestKit.Example("hello");
        var starts = PluginSession.CountStartsInThisFlow();
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, PluginTestKit.NewScratch()) with { Policy = PluginPolicy.Resolve(install, true, Env) });
        Assert.Equal(PluginFailureKind.DisabledByPolicy, started.Failure!.Kind);
        Assert.Equal(0, starts.Value);
    }

    /// <summary>P-6: a workspace that arrives saying a plugin is trusted changes nothing — trust is read from the
    /// user's preferences alone.</summary>
    [Fact]
    public void AWorkspaceCarryingATrustEntryIsIgnored()
    {
        var entry = PluginTestKit.Example("hello");
        string ws = PluginTestKit.NewScratch();
        File.WriteAllText(Path.Combine(ws, ".cws"),
            $$"""{ "{{PluginTrustStore.PreferenceKey}}": { {{System.Text.Json.JsonSerializer.Serialize(entry.Directory)}}: true } }""");
        string prefs = Path.Combine(PluginTestKit.NewScratch(), "preferences.json");
        File.WriteAllText(prefs, "{}");

        var trust = PluginTrustStore.FromRecordedPreferences(prefs);
        Assert.Equal(PluginTrustDecision.Unknown, trust.Decide(entry.Directory));

        var starts = PluginSession.CountStartsInThisFlow();
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, PluginTestKit.NewScratch()) with { Trust = trust, WorkspaceDir = ws });
        Assert.Equal(PluginFailureKind.NotPermitted, started.Failure!.Kind);
        Assert.Equal(0, starts.Value);
    }
}
