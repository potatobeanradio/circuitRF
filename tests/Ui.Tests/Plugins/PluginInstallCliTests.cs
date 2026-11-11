using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Plugins;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;
using static CircuitRF.Ui.Tests.Plugins.PluginCliVerbTests;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-6 gate 7: <c>plugin install</c> and <c>uninstall</c> as a process, and the MCP <c>plugin</c> tool's install —
/// which puts files in place and runs nothing, after which only a grant recorded the way Settings ▸ Plugins records it
/// lets the same <c>run</c> succeed, with no server restart. In the per-user-state collection because the grant is
/// written through the application's own preferences.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class PluginInstallCliTests
{
    private const string HelloId = "example.hello";

    [Fact]
    public async Task InstallAsAProcess_GivesTheTreeTheInProcessInstallerGives_AndUninstallRemovesIt()
    {
        var env = Env(allow: []);
        string source = CopyHello(env.Scratch);

        var install = PluginCliVerbTests.Cli(env, "plugin", "install", source);
        Assert.True(install.Exit == 0, install.StdErr);
        Assert.Contains("will not run until the person allows it in circuitRF", install.StdErr);
        string viaCli = Path.Combine(env.State, "plugins", HelloId, "1.0.0");

        string root = Path.Combine(env.Scratch, "in-process");
        var direct = await PluginInstaller.InstallFolderAsync(new PluginInstallContext(root, Path.Combine(env.Scratch, "p.json"), PluginInstallOrigin.Cli), source);
        Assert.True(direct.Ok, direct.Refusal);

        Assert.Equal(Tree(viaCli), Tree(direct.Directory!));
        Assert.False(Directory.Exists(Path.Combine(viaCli, PluginInstaller.VenvFolder)), "an install outside the window makes no environment");
        var record = PluginInstaller.ReadActive(Path.GetDirectoryName(viaCli)!)!;
        Assert.Equal(("cli", PluginActiveRecord.Pending), (record.Origin, record.Environment));

        var uninstall = PluginCliVerbTests.Cli(env, "plugin", "uninstall", HelloId);
        Assert.True(uninstall.Exit == 0, uninstall.StdErr);
        Assert.False(Directory.Exists(Path.GetDirectoryName(viaCli)));
    }

    [PythonFact]
    public async Task OverMcp_InstallRunsNothing_RunWaitsForTheGrant_AndUninstallRemovesItsPermission()
    {
        var env = Env(allow: []);
        string source = CopyHello(env.Scratch);
        using var server = new Serve(env);

        var tool = JsonNode.Parse(server.Request("tools/list", new JsonObject()))!["result"]!["tools"]!.AsArray()
            .Single(t => t!["name"]!.GetValue<string>() == "plugin")!;
        Assert.True((bool)tool["annotations"]!["destructiveHint"]! && (bool)tool["annotations"]!["openWorldHint"]!);

        var outside = server.Call(new JsonObject { ["action"] = "install", ["source"] = PluginTestKit.Example("hello").Directory });
        Assert.NotEqual(0, outside["exitCode"]?.GetValue<int>() ?? 1);

        var installed = server.Call(new JsonObject { ["action"] = "install", ["source"] = Path.GetFileName(source) });
        Assert.True(installed["exitCode"]!.GetValue<int>() == 0, installed.ToJsonString());
        Assert.Contains(installed["diagnostics"]!.AsArray(), d => (string?)d!["id"] == "plugin.permission.needs-user");
        string dir = installed["result"]!["plugin"]!["directory"]!.GetValue<string>();
        Assert.False(Directory.Exists(Path.Combine(dir, PluginInstaller.VenvFolder)), "an install over MCP makes no environment");
        Assert.Equal("mcp", PluginInstaller.RecordFor(dir)!.Origin);

        var refused = server.Call(new JsonObject { ["action"] = "run", ["id"] = HelloId });
        Assert.Equal(1, refused["exitCode"]!.GetValue<int>());
        Assert.Contains(refused["diagnostics"]!.AsArray(), d => (string?)d!["id"] == "plugin.permission.needs-user");

        // Settings ▸ Plugins' Allowed: PluginAllow makes the environment, then records the permission by its one key.
        AppDataRoot.RedirectTo(env.State);
        AppPreferencesIo.InvalidateCache();
        try
        {
            Assert.Null(await PluginInstaller.PrepareEnvironmentAsync(PluginHost.InstallContext(null, null) with
            { PythonCommand = CircuitRF.Design.Layout.PCells.Wire.PythonInterpreterDiscovery.Find(null, PythonRunner.Interpreter, out _) }, dir));
            PluginHost.RecordPermission(dir, true);
        }
        finally { AppDataRoot.RedirectTo(null); AppPreferencesIo.InvalidateCache(); }

        var ran = server.Call(new JsonObject { ["action"] = "run", ["id"] = HelloId });
        Assert.True(ran["exitCode"]!.GetValue<int>() == 0, ran.ToJsonString());

        var removed = server.Call(new JsonObject { ["action"] = "uninstall", ["id"] = HelloId });
        Assert.True(removed["exitCode"]!.GetValue<int>() == 0, removed.ToJsonString());
        Assert.False(Directory.Exists(dir));
        Assert.Equal(PluginTrustDecision.Unknown, PluginTrustStore.FromRecordedPreferences(env.Preferences).Decide(dir));
    }

    private static string CopyHello(string scratch)
    {
        string dir = Path.Combine(scratch, "hello-src");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(PluginTestKit.Example("hello").Directory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    /// <summary>Every file's bytes by relative path — <c>active.json</c> less the time and origin, which differ by design.</summary>
    private static string Tree(string versionDir)
    {
        var parts = Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(versionDir, f) + ":" + Convert.ToHexString(File.ReadAllBytes(f))).ToList();
        var active = PluginInstaller.ReadActive(Path.GetDirectoryName(versionDir)!)!;
        parts.Add($"active:{active.Active}|{active.Previous}|{active.Started}|{active.Environment}|{active.Source.Split(' ')[0]}");
        return string.Join("\n", parts);
    }
}
