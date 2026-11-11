using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Plugins;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>brief-plg-6's installer gates 1-6, on the functions Settings ▸ Plugins, the command line and MCP all call.
/// Every download goes through a counting seam that answers from memory: nothing here reaches a network.</summary>
public class PluginInstallerTests
{
    private const string Id = "example.hello";

    // ── gate 1: atomic ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("bad-zip", "is not a readable zip archive")]
    [InlineData("mismatch", "says it is version 3.0.0 of example.hello, and 2.0.0 was asked for")]
    [InlineData("cancel", "Cancelled")]
    public async Task Gate1_AFailedInstall_LeavesThePreviousVersionActive_AndNoPartialFolder(string how, string said)
    {
        var (root, ctx) = Store();
        Assert.True((await PluginInstaller.InstallFolderAsync(ctx, Plugin("1.0.0"))).Ok);

        byte[] zip = how == "bad-zip" ? Encoding.ASCII.GetBytes("not a zip at all") : Zip(Plugin("3.0.0"));
        var http = new CountingHttp { ["https://github.com/example/hello/releases/download/v2/hello.zip"] = zip };
        using var cancelled = new CancellationTokenSource();
        if (how == "cancel") cancelled.Cancel();

        var result = await PluginInstaller.InstallFromListAsync(ctx with { Http = http }, Index("2.0.0", Sha(zip)), Id, null, cancelled.Token);

        Assert.False(result.Ok);
        Assert.Contains(said, result.Refusal);
        AssertOnlyActive(root, "1.0.0");
    }

    [PythonFact]
    public async Task Gate1_ARequirementThatFails_IsARefusalNamingIt_AndRollsTheInstallBack()
    {
        var (root, ctx) = Store();
        Assert.True((await PluginInstaller.InstallFolderAsync(ctx, Plugin("1.0.0"))).Ok);
        string v2 = Plugin("2.0.0");
        File.WriteAllText(Path.Combine(v2, "requirements.txt"), "./no-such-package\n");

        var result = await PluginInstaller.InstallFolderAsync(ctx with { CreatePythonEnvironment = true, PythonCommand = Python() }, v2);

        Assert.False(result.Ok);
        Assert.Contains("could not be installed. pip said:", result.Refusal);
        AssertOnlyActive(root, "1.0.0");
    }

    // ── one Python, one shared plugin environment ───────────────────────────────

    /// <summary>
    /// A plugin with no requirements makes no environment; two whose requirements agree share ONE environment and one copy
    /// of the package; one whose requirement conflicts gets its own, and the shared one is left as it was. Offline: the
    /// package is a wheel built here, found through the requirements' own <c>--no-index --find-links</c>.
    /// </summary>
    [PythonFact]
    public async Task PluginsShareOnePythonEnvironment_UnlessTheirRequirementsConflict()
    {
        var (root, ctx) = Store();
        ctx = ctx with { CreatePythonEnvironment = true, PythonCommand = Python() };
        string wheels = Path.Combine(root, "..", "wheels");
        Wheel(wheels, "1.0"); Wheel(wheels, "2.0");

        var bare = await PluginInstaller.InstallFolderAsync(ctx, Plugin("1.0.0"));
        Assert.Equal(PluginActiveRecord.Ready, bare.Environment);
        Assert.False(Directory.Exists(Path.Combine(root, PluginPythonEnvironment.SharedFolder)), "no requirements, no environment");

        string Needs(string id, string version)
        {
            string dir = Plugin("1.0.0", id);
            File.WriteAllText(Path.Combine(dir, "requirements.txt"), $"--no-index\n--find-links {Path.GetFullPath(wheels)}\ncrfdemo=={version}\n");
            return dir;
        }
        var a = await PluginInstaller.InstallFolderAsync(ctx, Needs("example.a", "1.0"));
        var b = await PluginInstaller.InstallFolderAsync(ctx, Needs("example.b", "1.0"));
        Assert.Equal((PluginActiveRecord.Shared, PluginActiveRecord.Shared), (a.Environment, b.Environment));

        var c = await PluginInstaller.InstallFolderAsync(ctx, Needs("example.c", "2.0"));
        Assert.True(c.Ok, c.Refusal);
        Assert.Equal(PluginActiveRecord.Own, c.Environment);
        Assert.True(File.Exists(PluginPythonEnvironment.Interpreter(c.Directory!)));
        Assert.False(Directory.Exists(Path.Combine(a.Directory!, PluginInstaller.VenvFolder)));
        Assert.Single(Directory.GetDirectories(Path.Combine(root, PluginPythonEnvironment.SharedFolder), "crfdemo-1.0.dist-info", SearchOption.AllDirectories));
    }

    // ── gate 2: roll back ───────────────────────────────────────────────────────

    [PythonFact]
    public async Task Gate2_AnUpdateThatFailsHello_IsRolledBackInOneStep_AndTheOldVersionRuns()
    {
        var (root, ctx) = Store();
        ctx = ctx with { CreatePythonEnvironment = true, PythonCommand = Python() };
        Assert.True((await PluginInstaller.InstallFolderAsync(ctx, Plugin("1.0.0"))).Ok);
        string v2 = Plugin("2.0.0");
        File.WriteAllText(Path.Combine(v2, "hello.py"), "import sys\nsys.exit(3)\n");
        Assert.True((await PluginInstaller.InstallFolderAsync(ctx, v2)).Ok);

        string idDir = Path.Combine(root, Id);
        Assert.Equal(("2.0.0", "1.0.0"), (PluginInstaller.ReadActive(idDir)!.Active, PluginInstaller.ReadActive(idDir)!.Previous));
        Assert.False(await Hello(root), "version 2 should fail hello");
        Assert.True(Directory.Exists(Path.Combine(idDir, "1.0.0")), "the previous version is kept until the new one has started once");

        var back = PluginInstaller.Rollback(ctx, Id);
        Assert.True(back.Ok, back.Refusal);
        Assert.True(await Hello(root), "version 1 should run after the roll back");
        Assert.Equal("1.0.0", PluginInstaller.ReadActive(idDir)!.Active);
        Assert.False(Directory.Exists(Path.Combine(idDir, "2.0.0")));
    }

    // ── gate 3: integrity ───────────────────────────────────────────────────────

    [Fact]
    public async Task Gate3_AnAssetWhoseSha256Differs_IsRefusedBeforeAnythingIsUnpacked()
    {
        var (root, ctx) = Store();
        byte[] zip = Zip(Plugin("2.0.0"));
        var http = new CountingHttp { ["https://github.com/example/hello/releases/download/v2/hello.zip"] = zip };

        var result = await PluginInstaller.InstallFromListAsync(ctx with { Http = http }, Index("2.0.0", new string('0', 64)), Id);

        Assert.False(result.Ok);
        Assert.Contains("does not match the list", result.Refusal);
        Assert.False(Directory.Exists(Path.Combine(root, Id)), "nothing may be unpacked from an asset that does not match");
    }

    [Fact]
    public async Task Gate3_AListWithABadSignature_IsRefused_AndTheShippedListStaysInUse()
    {
        var (root, _) = Store();
        using var release = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string key = Convert.ToBase64String(release.ExportSubjectPublicKeyInfo());
        byte[] list = Encoding.UTF8.GetBytes(IndexJson("2.0.0", new string('a', 64)));
        string cache = PluginInstaller.IndexCache(root);

        var forged = new CountingHttp { [PluginIndex.MainUrl] = list, [PluginIndex.MainUrl + ".sig"] = Sign(forger, list) };
        var (refused, why) = await PluginIndex.RefreshAsync(forged, cache, key);
        Assert.Null(refused);
        Assert.Contains("signature does not verify", why);
        Assert.Equal(PluginIndexOrigin.Shipped, PluginIndex.Current(cache, key).Origin);

        var signed = new CountingHttp { [PluginIndex.MainUrl] = list, [PluginIndex.MainUrl + ".sig"] = Sign(release, list) };
        var (index, _) = await PluginIndex.RefreshAsync(signed, cache, key);
        Assert.NotNull(index);
        Assert.Equal(PluginIndexOrigin.Refreshed, PluginIndex.Current(cache, key).Origin);
        Assert.Equal(PluginIndexOrigin.Shipped, PluginIndex.Current(cache, Convert.ToBase64String(forger.ExportSubjectPublicKeyInfo())).Origin);
    }

    // ── gate 4: zip safety ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("../escape.txt", "'../escape.txt' leads outside the plugin's folder")]
    [InlineData("/etc/absolute.txt", "'/etc/absolute.txt' is an absolute path")]
    public async Task Gate4_AnUnsafeEntry_IsRefusedByName(string entry, string said)
    {
        var (root, ctx) = Store();
        string file = Path.Combine(root, "unsafe.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            foreach (string f in Directory.GetFiles(Plugin("1.0.0"))) zip.CreateEntryFromFile(f, Path.GetFileName(f));
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write("x");
        }

        var result = await PluginInstaller.InstallZipAsync(ctx, file, "test");

        Assert.False(result.Ok);
        Assert.Contains(said, result.Refusal);
        Assert.False(Directory.Exists(Path.Combine(root, Id)));
    }

    /// <summary>Review fix: two links that each read as inside — one to '.', one through it to '..' — lead out together.
    /// Links are made last and checked by where they really lead, so nothing is written outside the plugin's folder.</summary>
    [Fact]
    public async Task Gate4_LinksThatLeadOutOnlyTogether_AreRefused_AndNothingIsWrittenOutside()
    {
        if (OperatingSystem.IsWindows()) return;   // an archive's links are left out there
        var (root, ctx) = Store();
        string file = Path.Combine(root, "chain.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            foreach (string f in Directory.GetFiles(Plugin("1.0.0"))) zip.CreateEntryFromFile(f, Path.GetFileName(f));
            void Link(string name, string target)
            {
                var e = zip.CreateEntry(name);
                e.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using var w = new StreamWriter(e.Open());
                w.Write(target);
            }
            // "d/e" reads as d/.. — the folder itself — but d is the folder, so it is the folder above.
            Link("d", ".");
            Link("d/e", "..");
        }

        var result = await PluginInstaller.InstallZipAsync(ctx, file, "test");

        Assert.False(result.Ok);
        Assert.Contains("leads outside the plugin's folder", result.Refusal);
        Assert.False(Directory.Exists(Path.Combine(root, Id)));
    }

    /// <summary>Review fix: a folder holding a link to a folder above it is installed with the link as a link, not copied
    /// into itself without end.</summary>
    [Fact]
    public async Task InstallFolder_CopiesALinkToAFolderAboveIt_AsALink()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, ctx) = Store();
        string from = Plugin("1.0.0");
        Directory.CreateDirectory(Path.Combine(from, "a", "b"));
        Directory.CreateSymbolicLink(Path.Combine(from, "a", "b", "up"), "..");

        var install = PluginInstaller.InstallFolderAsync(ctx, from);
        Assert.True(await Task.WhenAny(install, Task.Delay(TimeSpan.FromSeconds(20))) == install, "the copy did not finish");
        var result = await install;

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal("..", new DirectoryInfo(Path.Combine(result.Directory!, "a", "b", "up")).LinkTarget);
    }

    /// <summary>Review fix: an id or version names a folder under the plugins root and nothing else, so '..' or an absolute
    /// path is refused before anything is deleted — the command line and MCP pass them through as typed.</summary>
    [Theory]
    [InlineData("..", null)]
    [InlineData("/", null)]
    [InlineData(Id, "../..")]
    public void Uninstall_OfAnIdOrVersionThatIsNotAFolderName_IsRefused_AndDeletesNothing(string id, string? version)
    {
        var (root, ctx) = Store();
        string beside = Path.Combine(Path.GetDirectoryName(root)!, "keep.txt");
        File.WriteAllText(beside, "x");
        Directory.CreateDirectory(Path.Combine(root, Id, "1.0.0"));

        var result = PluginInstaller.Uninstall(ctx, id, version);

        Assert.False(result.Ok);
        Assert.Contains("is not a plugin", result.Refusal);
        Assert.True(File.Exists(beside));
        Assert.True(Directory.Exists(Path.Combine(root, Id, "1.0.0")));
    }

    // ── gate 5: no unasked network ──────────────────────────────────────────────

    [Fact]
    public async Task Gate5_ListingTheTabAndStartingPluginsMakeNoRequest_OnlyRefreshAndInstallDo()
    {
        var (root, ctx) = Store();
        var http = new CountingHttp();
        Assert.True((await PluginInstaller.InstallFolderAsync(ctx with { Http = http }, Plugin("1.0.0"))).Ok);

        var sources = new PluginCatalogSources(root);
        var index = PluginIndex.Current(PluginInstaller.IndexCache(root), null);
        var rows = PluginsSettingsModel.Installed(sources, new PluginTrustStore(), index);
        PluginsSettingsModel.Available(index, rows);
        var entry = PluginCatalog.Discover(sources).Single();
        if (PluginSession.Start(entry, PluginTestKit.Allowed(entry, root)).Value is { } session) await session.DisposeAsync();
        Assert.Equal(0, http.Requests);

        await PluginIndex.RefreshAsync(http, PluginInstaller.IndexCache(root), null);
        Assert.True(http.Requests > 0, "Refresh is the request the person asked for");
    }

    // ── gate 6: a linked folder runs in place ───────────────────────────────────

    [Fact]
    public void Gate6_ALinkedFolder_IsReReadAtTheNextStart_WithoutReinstalling()
    {
        var (root, ctx) = Store();
        string folder = Plugin("1.0.0");
        Assert.True(PluginInstaller.Link(ctx, folder).Ok);
        var prefs = PluginPreferences.Read(ctx.PreferencesPath);

        var before = PluginCatalog.Discover(new PluginCatalogSources(root, prefs.LinkedFolders)).Single();
        Assert.Equal(Path.GetFullPath(folder), before.Directory);
        Assert.Equal(PluginSource.Linked, before.Source);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "plugin.json")))!;
        manifest["name"] = "Hello Divider, edited";
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest.ToJsonString());

        Assert.Equal("Hello Divider, edited", PluginCatalog.Read(before.Directory, before.Source).DisplayName);
        Assert.False(Directory.Exists(Path.Combine(root, Id)), "a link copies nothing");
    }

    // ── uninstall ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Uninstall_RemovesTheFolder_ItsPermission_AndWhatWasRememberedForIt()
    {
        var (root, ctx) = Store();
        var installed = await PluginInstaller.InstallFolderAsync(ctx, Plugin("1.0.0"));
        File.WriteAllText(ctx.PreferencesPath, new JsonObject
        {
            ["theme"] = "Default",
            [PluginTrustStore.PreferenceKey] = new JsonObject { [PluginTrustStore.Key(installed.Directory!)] = true },
            [PluginPreferences.DialogWidthsKey] = new JsonObject { [Id] = 500 },
            [PluginPreferences.LastParamsKey] = new JsonObject { [Id + "|/ws"] = new JsonObject { ["r"] = "50" } },
        }.ToJsonString());

        Assert.True(PluginInstaller.Uninstall(ctx, Id).Ok);

        var prefs = JsonNode.Parse(File.ReadAllText(ctx.PreferencesPath))!.AsObject();
        Assert.False(Directory.Exists(Path.Combine(root, Id)));
        Assert.Empty(prefs[PluginTrustStore.PreferenceKey]!.AsObject());
        Assert.Empty(prefs[PluginPreferences.DialogWidthsKey]!.AsObject());
        Assert.Empty(prefs[PluginPreferences.LastParamsKey]!.AsObject());
        Assert.Equal("Default", (string?)prefs["theme"]);
    }

    // ── links a user pastes ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/example/hello", "GitHub", "example/hello", null)]
    [InlineData("https://github.com/example/hello.git", "GitHub", "example/hello", null)]
    [InlineData("git@github.com:example/hello.git", "GitHub", "example/hello", null)]
    [InlineData("https://github.com/example/hello/releases/tag/v1.2.0", "GitHub", "example/hello", "v1.2.0")]
    [InlineData("https://gitlab.com/group/sub/hello", "GitLab", "group/sub/hello", null)]
    [InlineData("https://gitlab.com/group/hello/-/releases/v2.0", "GitLab", "group/hello", "v2.0")]
    [InlineData("https://gitlab.com/group/hello/-/tags/v3", "GitLab", "group/hello", "v3")]
    public void APastedLink_NamesItsRepository_AndTheTagWhenItHasOne(string link, string host, string path, string? tag)
    {
        var (repo, t) = PluginRepository.Parse(link)!.Value;
        Assert.Equal((host, path, tag), (repo.Host.ToString(), repo.Path, t));
    }

    [Theory]
    [InlineData("https://example.com/owner/repo")]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    public void ALinkToNeitherHost_IsNotARepository(string link) => Assert.Null(PluginRepository.Parse(link));

    // ══ harness ═════════════════════════════════════════════════════════════════

    private static (string Root, PluginInstallContext Ctx) Store()
    {
        string scratch = PluginTestKit.NewScratch(), root = Path.Combine(scratch, "plugins");
        Directory.CreateDirectory(root);
        return (root, new PluginInstallContext(root, Path.Combine(scratch, "preferences.json"), PluginInstallOrigin.Settings));
    }

    /// <summary>A pure-Python wheel of a one-module package <c>crfdemo</c>, made by hand so no build tool or network is
    /// needed to install it.</summary>
    private static void Wheel(string dir, string version)
    {
        Directory.CreateDirectory(dir);
        using var zip = ZipFile.Open(Path.Combine(dir, $"crfdemo-{version}-py3-none-any.whl"), ZipArchiveMode.Create);
        void Put(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
        string info = $"crfdemo-{version}.dist-info";
        Put("crfdemo/__init__.py", $"VERSION = '{version}'\n");
        Put($"{info}/METADATA", $"Metadata-Version: 2.1\nName: crfdemo\nVersion: {version}\n");
        Put($"{info}/WHEEL", "Wheel-Version: 1.0\nGenerator: test\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
        Put($"{info}/RECORD", $"crfdemo/__init__.py,,\n{info}/METADATA,,\n{info}/WHEEL,,\n{info}/RECORD,,\n");
    }

    /// <summary>A copy of the hello example at <paramref name="version"/>, under <paramref name="id"/> when given.</summary>
    private static string Plugin(string version, string? id = null)
    {
        string dir = Path.Combine(PluginTestKit.NewScratch(), "hello");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(PluginTestKit.Example("hello").Directory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "plugin.json")))!;
        manifest["version"] = version;
        if (id is not null) manifest["id"] = id;
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest.ToJsonString());
        return dir;
    }

    private static byte[] Zip(string folder)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (string f in Directory.GetFiles(folder)) zip.CreateEntryFromFile(f, Path.GetFileName(f));
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static string IndexJson(string version, string sha) => $$"""
        { "schema": 1, "plugins": [ { "id": "{{Id}}", "name": "Hello Divider", "repo": "https://github.com/example/hello",
          "versions": [ { "version": "{{version}}", "tag": "v2", "apiVersion": 1,
            "assets": { "any": { "url": "https://github.com/example/hello/releases/download/v2/hello.zip", "sha256": "{{sha}}" } } } ] } ] }
        """;

    private static PluginIndex Index(string version, string sha) => PluginIndex.Read(Encoding.UTF8.GetBytes(IndexJson(version, sha))).Index!;

    private static byte[] Sign(ECDsa key, byte[] data)
        => Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));

    private static void AssertOnlyActive(string root, string version)
    {
        string idDir = Path.Combine(root, Id);
        Assert.Equal(version, PluginInstaller.ReadActive(idDir)!.Active);
        Assert.Empty(Directory.GetDirectories(idDir, "*" + PluginInstaller.PartialSuffix));
        Assert.Empty(Directory.GetDirectories(root, ".download-*"));
    }

    private static PythonInterpreter Python()
        => PythonInterpreterDiscovery.Find(null, PythonRunner.Interpreter, out _) ?? throw new InvalidOperationException("no Python");

    /// <summary>Starts the active version as the catalogue offers it and asks <c>hello</c>.</summary>
    private static async Task<bool> Hello(string root)
    {
        var entry = PluginCatalog.Discover(new PluginCatalogSources(root)).Single();
        var options = PluginTestKit.Allowed(entry, Path.Combine(root, "..")) with { PythonCommand = null };
        var started = PluginSession.Start(entry, options);
        if (!started.Ok) return false;
        await using var session = started.Value!;
        return (await session.HelloAsync()).Ok;
    }

    /// <summary>Answers from memory and counts every request — gate 5's seam.</summary>
    private sealed class CountingHttp : IPluginHttp
    {
        private readonly Dictionary<string, byte[]> _bodies = [];
        public int Requests;
        public byte[] this[string url] { set => _bodies[url] = value; }

        public Task<PluginHttpResult> GetBytesAsync(string url, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PluginHttpResult(_bodies.GetValueOrDefault(url)));
        }

        public async Task<string?> DownloadAsync(string url, string file, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            ct.ThrowIfCancellationRequested();
            if (!_bodies.TryGetValue(url, out var body)) return $"{url} answered 404 Not Found.";
            await File.WriteAllBytesAsync(file, body, ct);
            return null;
        }
    }
}
