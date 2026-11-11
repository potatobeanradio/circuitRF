using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-5's gates: <c>circuitRF plugin</c> and the MCP <c>plugin</c> tool, driven as the built CLI process with a
/// state directory and a plugin path of their own (<c>CRF_STATE_DIR</c>, <c>CRF_PLUGIN_PATH</c>), so nothing here reads
/// or writes the developer's own preferences.
/// </summary>
public class PluginCliVerbTests
{
    private const string StubId = "example.single-stub";

    // ── gate 1: headless and panel agree ────────────────────────────────────────

    [PythonFact]
    public async Task Gate1_RunPlace_AndThePanelsPlaceInWorkspace_GiveByteIdenticalCells()
    {
        var env = Env(allow: ["stub"]);
        string headless = Workspace(env, "headless"), panel = Workspace(env, "panel");

        var run = Cli(env, "plugin", "run", StubId, "--place", headless);
        Assert.True(run.Exit == 0, run.StdErr);

        // The panel: its own update loop over a real session, and the commit the window wires — commit, then
        // PluginPlacement with the layout view read from the reply the panel last drew.
        var entry = PluginTestKit.Example("stub");
        PluginPanelController controller = null!;
        controller = new PluginPanelController(entry.Manifest!, () =>
        {
            var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, env.Scratch) with { WorkspaceDir = panel });
            return started.Ok ? PluginOutcome<IPluginPanelSession>.Of(started.Value!) : PluginOutcome<IPluginPanelSession>.Fail(started.Failure!);
        }, new PluginPanelOptions
        {
            Context = new PluginParamContext(panel),
            Debounce = TimeSpan.FromMilliseconds(10),
            Commit = async (session, values, cancel) =>
            {
                var placed = await session.CommitAsync(values, cancel);
                Assert.True(placed.Ok, placed.Failure?.Sentence);
                var cell = Assert.Single(placed.Value!.Cells);
                var result = PluginPlacement.Place(new PluginPlacementRequest(panel, entry.Manifest!, cell, placed.Value.Messages,
                    Layout: PluginPlacement.LayoutViewFor(controller.State.ViewBodies.GetValueOrDefault(cell.LayoutView!), cell.LayoutView!, entry.Manifest!, panel)));
                Assert.Equal(PluginPlacementOutcome.Placed, result.Outcome);
                return [];
            },
        });
        await using (controller)
        {
            controller.Open();
            await Until(() => controller.State.Reply is not null, "the panel's first reply");
            controller.Commit();
            await Until(() => Directory.Exists(Path.Combine(panel, "stub", "layout")) && !controller.State.Committing, "the panel's placement");
        }

        var a = Files(Path.Combine(headless, "stub"));
        var b = Files(Path.Combine(panel, "stub"));
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        Assert.Contains("layout/artwork.clay", a.Keys);
        Assert.Contains("schematic/stub.cnl", a.Keys);
        foreach (string k in a.Keys) Assert.True(a[k].SequenceEqual(b[k]), $"{k} differs between plugin run --place and the panel");
    }

    // ── gate 2: no logic in the verb ────────────────────────────────────────────

    [Fact]
    public void Gate2_PluginCs_HoldsNoFrameHandlingNoPlacementStepsAndNoManifestParsing()
    {
        string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "Plugin.cs")));
        foreach (string forbidden in new[]
                 {
                     // frames and the conversation
                     "PCellWire", "HelloAsync", "UpdateAsync", "CommitAsync", "ByeAsync", "PluginSession.Start", "\"type\"", "\"hello\"", "\"bye\"",
                     // placement steps
                     "CellCreate", "WorkspaceCheckpoints", "PluginPlacement.", "File.Copy", "File.Move", "Directory.CreateDirectory",
                     // manifest parsing
                     "PluginManifestReader.Read", "JsonDocument", "JsonNode.Parse", "GetProperty(", "PluginLayoutReader.Read(m.UiJson",
                     // installing (brief-plg-6 §5): the store's steps are PluginInstaller's alone
                     "ZipFile", "ZipArchive", "SHA256", "Directory.Move", "Directory.Delete", "active.json", ".partial", "venv", "pip",
                 })
            Assert.DoesNotContain(forbidden, code);
        Assert.Contains("PluginHeadless.RunAsync(", code);
        Assert.Contains("PluginInstaller.InstallFolderAsync(", code);
        Assert.Contains("PluginInstaller.Uninstall(", code);
        Assert.Contains("PluginCatalog.Discover(", code);
    }

    // ── gate 3: files from views ────────────────────────────────────────────────

    [PythonFact]
    public void Gate3_RunO_WritesACddAndAClay_ThatRenderDrawsWithoutAWarning()
    {
        var env = Env(allow: ["stub"]);
        string ws = Workspace(env, "ws"), outDir = Path.Combine(env.Scratch, "out");

        var run = Cli(env, "plugin", "run", StubId, "--workspace", ws, "-o", outDir);
        Assert.True(run.Exit == 0, run.StdErr);
        foreach (string f in new[] { "response.cdd", "response.npy", "match.cdd", "match.npy", "dims.csv", "artwork.clay", "circuit.cnl", "readouts.json" })
            Assert.True(File.Exists(Path.Combine(outDir, f)), $"{f} was not written");

        var plot = Cli(env, "render", Path.Combine(outDir, "response.cdd"), "--data", Path.Combine(outDir, "response.npy"), "-o", Path.Combine(env.Scratch, "response.svg"));
        Assert.True(plot.Exit == 0, plot.StdErr);
        Assert.DoesNotContain("warning", plot.StdErr, StringComparison.OrdinalIgnoreCase);

        var layout = Cli(env, "render", Path.Combine(outDir, "artwork.clay"), "-o", Path.Combine(env.Scratch, "artwork.svg"));
        Assert.True(layout.Exit == 0, layout.StdErr);
        Assert.DoesNotContain("warning", layout.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("note", layout.StdErr, StringComparison.OrdinalIgnoreCase);   // its technology resolved
    }

    // ── gate 4: refusals ────────────────────────────────────────────────────────

    /// <summary>Each refused before a process starts: the sentence, and exit 1.</summary>
    [Theory]
    [InlineData("not-permitted", "Single-Stub Match (example.single-stub) is installed but the person has not allowed it to run on this machine. Only they can, in circuitRF")]
    [InlineData("policy",        "Plugins are disabled by CRF_NO_PLUGINS=1 in this environment. That overrides any plugin's permission")]
    [InlineData("unknown",       "bogus: the plugin declares no such parameter. Its parameters are f0, rl, xl, substrate, trim.")]
    [InlineData("range",         "Frequency: 50 GHz is above the maximum 40 GHz; it takes 10 MHz to 40 GHz.")]
    [InlineData("window",        "Own Window is a window plugin with no headless form")]
    public void Gate4_EachRefusal_SaysWhy_AndExits1(string which, string sentence)
    {
        var env = Env(allow: which == "not-permitted" ? [] : ["stub", "window"]);
        string ws = Workspace(env, "ws");
        var run = which switch
        {
            "policy"  => Cli(env with { NoPlugins = true }, "plugin", "run", StubId, "--workspace", ws),
            "unknown" => Cli(env, "plugin", "run", StubId, "--workspace", ws, "--set", "bogus=1"),
            "range"   => Cli(env, "plugin", "run", StubId, "--workspace", ws, "--set", "f0=50 GHz"),
            "window"  => Cli(env, "plugin", "run", "example.own-window"),
            _         => Cli(env, "plugin", "run", StubId, "--workspace", ws),
        };
        Assert.Equal(1, run.Exit);
        Assert.Contains(sentence, run.StdErr);
    }

    [PythonFact]
    public void Gate4_AnExistingCell_IsARefusalNamingReplaceAndNewName_AndReplaceWritesOverIt()
    {
        var env = Env(allow: ["stub"]);
        string ws = Workspace(env, "ws");
        Directory.CreateDirectory(Path.Combine(ws, "stub"));

        var refused = Cli(env, "plugin", "run", StubId, "--place", ws);
        Assert.Equal(1, refused.Exit);
        Assert.Contains("already has a cell stub. --replace writes over its views", refused.StdErr);
        Assert.Contains("--new-name <name> places it under another name", refused.StdErr);
        Assert.False(Directory.Exists(Path.Combine(ws, "stub", "layout")));

        var replaced = Cli(env, "plugin", "run", StubId, "--place", ws, "--replace");
        Assert.True(replaced.Exit == 0, replaced.StdErr);
        Assert.True(File.Exists(Path.Combine(ws, "stub", "layout", "artwork.clay")));
    }

    /// <summary>Permission is granted only in the window (owner decision): the command line has no <c>allow</c> and
    /// writes nothing for one, and its <c>deny</c> records a refusal where the prompt records decisions, keeping every other
    /// setting — after which a run says only Settings ▸ Plugins changes it.</summary>
    [Fact]
    public void TheCommandLineCannotAllow_ItCanOnlyDeny()
    {
        var env = Env(allow: []);
        File.WriteAllText(env.Preferences, "{ \"theme\": \"Default\" }");
        string stub = PluginTestKit.Example("stub").Directory;

        var allow = Cli(env, "plugin", "allow", StubId);
        Assert.Equal(1, allow.Exit);
        Assert.Contains("Permission to run a plugin is granted only in circuitRF itself", allow.StdErr);
        Assert.Equal(PluginTrustDecision.Unknown, PluginTrustStore.FromRecordedPreferences(env.Preferences).Decide(stub));

        File.WriteAllText(env.Preferences, new JsonObject
        {
            ["theme"] = "Default",
            [PluginTrustStore.PreferenceKey] = new JsonObject { [PluginTrustStore.Key(stub)] = true },
        }.ToJsonString());
        var deny = Cli(env, "plugin", "deny", StubId);
        Assert.True(deny.Exit == 0, deny.StdErr);
        Assert.Equal(PluginTrustDecision.Denied, PluginTrustStore.FromRecordedPreferences(env.Preferences).Decide(stub));
        Assert.Equal("Default", JsonNode.Parse(File.ReadAllText(env.Preferences))!["theme"]!.GetValue<string>());

        var run = Cli(env, "plugin", "run", StubId);
        Assert.Equal(1, run.Exit);
        Assert.Contains("the person has refused it permission to run on this machine. Only they can change that, in circuitRF's Settings ▸ Plugins", run.StdErr);
    }

    // ── gate 5: MCP ─────────────────────────────────────────────────────────────

    [PythonFact]
    public void Gate5_TheMcpTool_ListsDescribesAndRunsTheStub()
    {
        var env = Env(allow: ["stub"]);
        string ws = Workspace(env, "ws");
        using var server = new Serve(env);

        var tool = JsonNode.Parse(server.Request("tools/list", new JsonObject()))!["result"]!["tools"]!.AsArray()
            .Single(t => t!["name"]!.GetValue<string>() == "plugin")!;
        var actions = tool["inputSchema"]!["properties"]!["action"]!["enum"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        // No allow and no deny: permission is granted only in the window (brief-plg-6 gate 7 adds install and uninstall).
        Assert.Equal(["list", "describe", "run", "install", "uninstall"], actions);

        var list = server.Call(new JsonObject { ["action"] = "list" });
        Assert.Contains(list["result"]!["plugin"]!["plugins"]!.AsArray(), p => p!["id"]!.GetValue<string>() == StubId);

        var describe = server.Call(new JsonObject { ["action"] = "describe", ["id"] = StubId });
        Assert.Equal(5, describe["result"]!["plugin"]!["manifest"]!["views"]!.AsArray().Count);

        var run = server.Call(new JsonObject
        {
            ["action"] = "run", ["id"] = StubId, ["workspace"] = ws, ["set"] = new JsonArray("f0=1 GHz"),
            ["output"] = Path.Combine(env.Scratch, "mcp-out"),
        });
        Assert.True(run["exitCode"]!.GetValue<int>() == 0, run.ToJsonString());
        Assert.Equal(1e9, run["result"]!["plugin"]!["params"]!["f0"]!["value"]!.GetValue<double>());
        Assert.True(File.Exists(Path.Combine(env.Scratch, "mcp-out", "artwork.clay")));

        // A plugin the person has not allowed: the notice the agent relays, by its id, naming the plugin.
        var refused = server.Call(new JsonObject { ["action"] = "run", ["id"] = "example.own-window" });
        Assert.Equal(1, refused["exitCode"]!.GetValue<int>());
        var notice = refused["diagnostics"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "plugin.permission.needs-user")!;
        Assert.Contains("Only they can, in circuitRF", notice["message"]!.GetValue<string>());
    }

    // ── gate 6: check ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_Check_ReportsADuplicateParameterAndALayoutNamingAnUndeclaredOne_Together()
    {
        var env = Env(allow: []);
        string dir = Path.Combine(env.Scratch, "bad-plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "apiVersion": 1, "id": "example.bad", "name": "Bad", "version": "0.1.0",
              "run": { "python": { "entry": "bad.py" } }, "kind": "panel",
              "params": [ { "name": "f0", "kind": "quantity", "unit": "Hz", "default": "1 GHz" },
                          { "name": "f0", "kind": "quantity", "unit": "Hz", "default": "2 GHz" } ],
              "ui": { "layout": { "column": [ { "param": "f0" }, { "param": "bw" } ] } } }
            """);

        var check = Cli(env, "check", dir);
        Assert.Equal(1, check.Exit);
        Assert.Contains("params.f0.name: 'f0' is declared twice.", check.StdErr);
        Assert.Contains("'bw' is not a declared parameter.", check.StdErr);
    }

    // ══ harness ═════════════════════════════════════════════════════════════════

    /// <param name="Scratch">This test's own folder.</param>
    /// <param name="Preferences">The preferences file the CLI reads and <c>deny</c> writes.</param>
    internal sealed record CliEnv(string Scratch, string State, string Preferences, bool NoPlugins = false);

    /// <summary>A state directory whose preferences allow the named example plugins, and a plugin path linking the stub
    /// and the window example.</summary>
    internal static CliEnv Env(string[] allow)
    {
        string scratch = PluginTestKit.NewScratch(), state = Path.Combine(scratch, "state");
        Directory.CreateDirectory(state);
        string prefs = Path.Combine(state, "preferences.json");
        if (allow.Length > 0)
        {
            var trust = new JsonObject();
            foreach (string name in allow) trust[PluginTrustStore.Key(PluginTestKit.Example(name).Directory)] = true;
            File.WriteAllText(prefs, new JsonObject { [PluginTrustStore.PreferenceKey] = trust }.ToJsonString());
        }
        return new CliEnv(scratch, state, prefs);
    }

    private static string Workspace(CliEnv env, string name)
    {
        string ws = Path.Combine(env.Scratch, name);
        string from = Path.Combine(RepoRoot(), "examples", "Patch Antenna");
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(ws, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return ws;
    }

    private static void Configure(ProcessStartInfo psi, CliEnv env)
    {
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = env.State;
        psi.Environment[PluginCatalogSources.EnvironmentVariable] =
            string.Join(Path.PathSeparator, PluginTestKit.Example("stub").Directory, PluginTestKit.Example("window").Directory);
        if (env.NoPlugins) psi.Environment[PluginPolicy.EnvironmentVariable] = "1";
        else psi.Environment.Remove(PluginPolicy.EnvironmentVariable);
    }

    internal static (int Exit, string StdOut, string StdErr) Cli(CliEnv env, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = env.Scratch, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        Configure(psi, env);
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary><c>serve --root &lt;scratch&gt;</c>, one request at a time; stderr drained on its own thread.</summary>
    internal sealed class Serve : IDisposable
    {
        private readonly Process _p;
        private readonly StringBuilder _err = new();
        private int _id;

        public Serve(CliEnv env)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = env.Scratch, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            };
            Configure(psi, env);
            foreach (string a in new[] { CliDll(), "serve", "--root", env.Scratch }) psi.ArgumentList.Add(a);
            _p = Process.Start(psi)!;
            _p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_err) _err.AppendLine(e.Data); };
            _p.BeginErrorReadLine();
        }

        public string Request(string method, JsonObject parameters)
        {
            int id = ++_id;
            _p.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
            _p.StandardInput.Flush();
            while (_p.StandardOutput.ReadLine() is { } line)
                if (JsonNode.Parse(line) is JsonObject frame && frame["id"]?.GetValue<int>() == id) return line;
            lock (_err) throw new Xunit.Sdk.XunitException("serve ended before answering:\n" + _err);
        }

        /// <summary>The <c>plugin</c> tool's document.</summary>
        public JsonNode Call(JsonObject arguments)
            => JsonNode.Parse(JsonNode.Parse(Request("tools/call", new JsonObject { ["name"] = "plugin", ["arguments"] = arguments }))!
                ["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

        public void Dispose()
        {
            try { _p.StandardInput.Close(); } catch { }
            if (!_p.WaitForExit(30_000)) _p.Kill(entireProcessTree: true);
            _p.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    private static Dictionary<string, byte[]> Files(string dir)
        => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes);

    internal static string StripComments(string code)
        => Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(PluginCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string dll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(dll), $"{dll} is not built.");
        return dll;
    }

    internal static string RepoRoot([CallerFilePath] string here = "")
    {
        string? dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }
}
