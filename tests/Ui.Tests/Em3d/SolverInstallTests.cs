using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using CircuitRF.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-24 §9 — the install assistant. No test downloads from the internet and no test builds
//  Palace: every "upstream" here is a local file:// archive of a stub program, and every install runs
//  into this class's own temporary root with a discovery instance of its own. Gate 1 moves the
//  process-wide state directory, which is why the class runs alone (UserStateDirectoryCollection).
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(UserStateDirectoryCollection.Name)]
public sealed class SolverInstallTests : IDisposable
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private const string Stub = "crf-fake-gmsh";
    private static string StubFile => Windows ? Stub + ".cmd" : Stub;

    private readonly string _tmp  = Path.Combine(Path.GetTempPath(), "crf-install-" + Guid.NewGuid().ToString("N")[..10]);
    private string Root => Path.Combine(_tmp, "solvers");

    public SolverInstallTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1. the root moved without moving ────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TheStateDirectoryBelowTheFirewallIsTheOneTheApplicationUses_WithAndWithoutTheRedirect()
    {
        Assert.Equal(UserStateDirectory.Dir, AppDataRoot.Dir);
        Assert.False(AppDataRoot.IsRedirected);
        try
        {
            AppDataRoot.RedirectTo(_tmp);
            Assert.Equal(Path.GetFullPath(_tmp), AppDataRoot.Dir);
            Assert.Equal(AppDataRoot.Dir, UserStateDirectory.Dir);
            Assert.True(UserStateDirectory.IsRedirected);
            // A redirected process never installs outside the redirect, spaces or not.
            Assert.Equal(Path.Combine(Path.GetFullPath(_tmp), "solvers"), SolverHomes.DefaultRoot);
        }
        finally
        {
            AppDataRoot.RedirectTo(null);
        }
        Assert.Equal(UserStateDirectory.Dir, AppDataRoot.Dir);
        Assert.False(SolverHomes.HasWhitespace(SolverHomes.DefaultRoot) && !UserStateDirectory.IsRedirected,
                     $"the unredirected root '{SolverHomes.DefaultRoot}' contains whitespace, which autotools refuses to build in");
    }

    // ── 2. recipe schema ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_EveryShippedRecipeValidatesAgainstTheSchema_AndParses_AndAPlantedTypoFailsBoth()
    {
        var schema = Schema();
        var recipes = SolverRecipes.ShippedText.Where(kv => !kv.Key.EndsWith("schema.json", StringComparison.Ordinal)).ToList();
        Assert.True(recipes.Count >= 10, $"only {recipes.Count} recipes are embedded — is the csproj glob still there?");

        foreach (var (name, text) in recipes)
        {
            using var doc = JsonDocument.Parse(text);
            var errors = schema.Validate(doc.RootElement);
            Assert.True(errors.Count == 0, $"{name}: {string.Join("; ", errors)}");
            var parsed = SolverRecipes.Parse(text, out string? error);
            Assert.True(parsed is not null, $"{name}: {error}");
            Assert.Equal(parsed!.Id + ".json", name);
            // Every shipped recipe is for a version discovery would accept.
            Assert.Contains(parsed.Version, SolverDiscovery.Create(parsed.Tool).ValidatedVersions.Select(v => v.Release));
        }
        Assert.Equal(recipes.Count, SolverRecipes.All.Count);

        // A typo in a key: the schema reports it, and the loader refuses it rather than skipping a step.
        string palace = recipes.First(r => r.Key.StartsWith("palace-", StringComparison.Ordinal)).Value;
        string typo = palace.Replace("\"captureAs\"", "\"captureas\"");
        using (var bad = JsonDocument.Parse(typo)) Assert.NotEmpty(schema.Validate(bad.RootElement));
        Assert.Null(SolverRecipes.Parse(typo, out string? why));
        Assert.Contains("captureas", why);

        // A typo in a value: an unknown step kind.
        string kind = palace.Replace("\"kind\": \"symlink\"", "\"kind\": \"symlnk\"");
        using (var bad = JsonDocument.Parse(kind)) Assert.NotEmpty(schema.Validate(bad.RootElement));
        Assert.Null(SolverRecipes.Parse(kind, out _));
    }

    // ── 3. end to end on a fake recipe ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AFakeRecipeGoesThroughConsentDownloadChecksumExtractPartialDiscoveryPublishAndRecord()
    {
        string archive = FakeArchive("4.15.2");
        var recipe = Recipe(archive, Sha(archive));
        var discovery = IsolatedDiscovery();
        var installer = Installer(discovery);
        string home = installer.HomeFor(recipe);

        string consent = installer.Consent(recipe);
        Assert.Contains(new Uri(archive).AbsoluteUri, consent);
        Assert.Contains(home, consent);
        Assert.Contains("can be cancelled", consent);
        Assert.False(Directory.Exists(Root), "consent created something");

        Directory.CreateDirectory(SolverHomes.Partial(home));   // debris from an earlier attempt
        var outcome = installer.Install(recipe, new RunControl { Total = SolverInstaller.WorkUnits(recipe) });

        Assert.True(outcome.Status == InstallStatus.Installed, outcome.Report);
        Assert.False(Directory.Exists(SolverHomes.Partial(home)));
        var record = InstallRecord.TryRead(Path.Combine(home, SolverHomes.RecordFile));
        Assert.NotNull(record);
        Assert.Equal(Path.Combine(home, "gmsh-fake", "bin", StubFile), record!.Program);
        Assert.Equal("4.15.2", record.Version);
        Assert.True(record.SizeBytes > 0);
        Assert.Single(record.Sources);
        Assert.True(record.Sources[0].Verified);

        var found = discovery.Find(out _);
        Assert.NotNull(found);
        Assert.Equal(SolverHowFound.Installed, found!.HowFound);
        Assert.Equal(record.Program, found.Path);
        Assert.StartsWith("Installed by circuitRF", discovery.DescribeForSettings(found, []));
        Assert.Contains("A 3D run now finds it", outcome.Report);

        // A second attempt does nothing.
        Assert.Equal(InstallStatus.AlreadyInstalled, installer.Install(recipe).Status);
    }

    // ── 4. checksum mismatch ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AWrongChecksumRefusesTheArchive_PublishesNothing_AndDiscoveryFindsNothing()
    {
        string archive = FakeArchive("4.15.2");
        var recipe = Recipe(archive, new string('0', 64));
        var discovery = IsolatedDiscovery();
        var installer = Installer(discovery);

        var outcome = installer.Install(recipe);

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("checksum", outcome.Report);
        Assert.Contains(new string('0', 64), outcome.Report);
        Assert.Contains("Download", outcome.FailedStep);
        Assert.False(Directory.Exists(installer.HomeFor(recipe)));
        Assert.Null(discovery.Find(out _));
        Assert.Equal(0, discovery.VersionProbesRun);
    }

    // ── 5. cancellation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_CancellingMidDownloadPublishesNothing_AndTheNextAttemptRemovesThePartial()
    {
        string archive = FakeArchive("4.15.2", padBytes: 4 * 1024 * 1024);
        var recipe = Recipe(archive, Sha(archive));
        var discovery = IsolatedDiscovery();
        var installer = Installer(discovery);
        string home = installer.HomeFor(recipe);

        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token    = cts.Token,
            Progress = new Inline(p => { if (p.StageDetail.Contains("MB", StringComparison.Ordinal)) cts.Cancel(); }),
        };
        var outcome = installer.Install(recipe, control);

        Assert.Equal(InstallStatus.Cancelled, outcome.Status);
        Assert.False(Directory.Exists(home));
        Assert.True(Directory.Exists(SolverHomes.Partial(home)), "the cancelled attempt's .partial should wait for the next attempt");
        Assert.Null(discovery.Find(out _));
        Assert.Equal(0, discovery.VersionProbesRun);   // no record, so not even a candidate

        File.WriteAllText(Path.Combine(SolverHomes.Partial(home), "left-by-the-cancelled-attempt"), "");
        var retry = installer.Install(recipe);
        Assert.True(retry.Status == InstallStatus.Installed, retry.Report);
        Assert.False(File.Exists(Path.Combine(home, "left-by-the-cancelled-attempt")));
        Assert.False(Directory.Exists(SolverHomes.Partial(home)));
    }

    [Fact]
    public void Gate5b_CancellingMidStepStopsTheProcess_PublishesNothing_AndDiscoveryFindsNothing()
    {
        if (Windows) return;   // the long-running step is /bin/sleep
        string archive = FakeArchive("4.15.2");
        var recipe = Recipe(archive, Sha(archive), extraStep: """{ "name": "Wait", "kind": "run", "command": "/bin/sleep", "arguments": ["60"] }""");
        var discovery = IsolatedDiscovery();
        var installer = Installer(discovery);

        long before = Em3dProcessLauncher.InstallStepsStarted;
        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token    = cts.Token,
            Progress = new Inline(p => { if (p.Stage == "Wait") cts.CancelAfter(200); }),
        };
        var clock = Stopwatch.StartNew();
        var outcome = installer.Install(recipe, control);

        Assert.Equal(InstallStatus.Cancelled, outcome.Status);
        Assert.Equal(1, Em3dProcessLauncher.InstallStepsStarted - before);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(50), "the step's process was not stopped");
        Assert.False(Directory.Exists(installer.HomeFor(recipe)));
        Assert.Null(discovery.Find(out _));
        Assert.Equal(0, discovery.VersionProbesRun);
    }

    // ── 6. discovery must agree ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_AnInstalledProgramThatReportsAnUnvalidatedVersionFailsTheInstall_AndNothingIsPublished()
    {
        string archive = FakeArchive("4.14.0");
        var recipe = Recipe(archive, Sha(archive));
        var discovery = IsolatedDiscovery();
        var installer = Installer(discovery);

        var outcome = installer.Install(recipe);

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Equal("Checking the installed program", outcome.FailedStep);
        Assert.Contains("4.14.0", outcome.Report);
        Assert.Contains("not validated", outcome.Report);
        Assert.False(Directory.Exists(installer.HomeFor(recipe)));
        Assert.Null(discovery.Find(out _));
    }

    // ── 7. known failures ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET https://mirror.spack.io/_source-cache/archive/ab/abc.tar.gz errored with: [SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed: unable to get local issuer certificate (_ssl.c:1018)", "spack-ssl-certificates", "SPACK_PYTHON")]
    [InlineData("==> Error: No such variant 'gkrand' in package metis", "spack-builtin-repo-too-old", "PR 4000")]
    [InlineData("The PETSc test program compiled, but CMake could not execute it.", "spack-target-emits-sve", "target=m3")]
    public void Gate7_EachOfF0sFailuresMatchesWithItsRemedyAndSource(string verbatim, string id, string remedyWord)
    {
        var match = KnownFailures.Match([verbatim]);
        Assert.NotNull(match);
        Assert.Equal(id, match!.Id);
        string sentence = KnownFailures.Explain(match);
        Assert.StartsWith("circuitRF's own installs have met this:", sentence);
        Assert.Contains(remedyWord, sentence);
        Assert.Contains("F0, macOS 27.0 on an Apple M4", sentence);
    }

    [Fact]
    public void Gate7b_AnInventedFailureMatchesNothing_AndAFailedStepIsReportedVerbatimWithItsLog()
    {
        Assert.Null(KnownFailures.Match(["error: the flux capacitor is misaligned"]));
        Assert.Equal("This is not a failure circuitRF has seen.", KnownFailures.Explain(null));
        Assert.Contains("Retrying often clears this", KnownFailures.Explain(KnownFailures.Match(["==> Error: ChecksumError: sha256 checksum failed for /x"])));

        if (OperatingSystem.IsWindows()) return;   // the failing step is a shell script
        string script = Path.Combine(_tmp, "fails.sh");
        File.WriteAllText(script, "#!/bin/sh\necho 'checking whether build environment is sane...'\n" +
                                  "echo \"configure: error: unsafe srcdir value: '/a b/src'\" >&2\nexit 2\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string archive = FakeArchive("4.15.2");
        var recipe = Recipe(archive, Sha(archive), extraStep: $$"""{ "name": "Configure", "kind": "run", "command": "{{script}}" }""");

        var outcome = Installer(IsolatedDiscovery()).Install(recipe);

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Equal("Configure", outcome.FailedStep);
        Assert.Contains("configure: error: unsafe srcdir value: '/a b/src'", outcome.Verbatim!);
        Assert.Contains("exited with code 2", outcome.Report);
        Assert.Equal("autotools-path-has-space", outcome.Known?.Id);
        Assert.True(File.Exists(outcome.LogPath), outcome.LogPath);
        Assert.Contains(outcome.LogPath!, outcome.Report);
        Assert.Contains("unsafe srcdir value", File.ReadAllText(outcome.LogPath!));
    }

    // ── 8. the user's Spack is untouched ────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_NoPalaceRecipeReferencesAPathOutsideItsHome_WithTheUsersOwnSpackPlanted()
    {
        string user = Path.Combine(_tmp, "user-home");
        Directory.CreateDirectory(Path.Combine(user, "opt", "spack", ".spack-db"));
        File.WriteAllText(Path.Combine(user, "opt", "spack", ".spack-db", "index.json"), "{}");
        Directory.CreateDirectory(Path.Combine(user, "spack", "bin"));
        Directory.CreateDirectory(Path.Combine(user, ".spack"));
        var inherited = new Dictionary<string, string>
        {
            ["HOME"] = user,
            ["PATH"] = Path.Combine(user, "spack", "bin") + ":/usr/bin:/bin",
            ["SPACK_ROOT"] = Path.Combine(user, "spack"),
            ["SPACK_ENV"] = Path.Combine(user, "envs", "palace"),
            ["SPACK_USER_CONFIG_PATH"] = Path.Combine(user, ".spack"),
            ["SPACK_PYTHON"] = Path.Combine(user, "python3"),
        };
        var installer = new SolverInstaller(Root) { InheritedEnvironment = () => inherited };
        var prerequisites = new Dictionary<string, string> { ["git"] = "/usr/bin/git", ["python"] = "/usr/bin/python3" };

        var palace = SolverRecipes.All.Where(r => r.Tool == SolverTool.Palace).ToList();
        Assert.True(palace.Count >= 2);
        foreach (var recipe in palace)
        {
            string home = installer.HomeFor(recipe);
            var plan = installer.ResolvedPlan(recipe, home, prerequisites);
            var env  = installer.StepEnvironment(recipe, new Dictionary<string, string>(prerequisites) { ["home"] = home });

            foreach (string line in plan)
                Assert.DoesNotContain(user, line);
            foreach (string path in plan.SelectMany(l => l.Split(' ')).Where(t => t.StartsWith('/')))
                if (path.Contains("spack", StringComparison.OrdinalIgnoreCase))
                    Assert.StartsWith(home, path);

            // The environment: every SPACK_ variable is the recipe's own, inside the home; nothing but the
            // login's own HOME still points into the user's directory.
            foreach (var (k, v) in env.Where(e => e.Key.StartsWith("SPACK_", StringComparison.Ordinal) && e.Key != "SPACK_PYTHON"
                                                                                                && e.Key != "SPACK_DISABLE_LOCAL_CONFIG"))
                Assert.StartsWith(home, v);
            Assert.Equal("/usr/bin/python3", env["SPACK_PYTHON"]);
            Assert.Equal("1", env["SPACK_DISABLE_LOCAL_CONFIG"]);
            Assert.False(env.ContainsKey("SPACK_ROOT"), recipe.Id);
            Assert.False(env.ContainsKey("SPACK_ENV"), recipe.Id);
            foreach (var (k, v) in env.Where(e => e.Key != "HOME"))
                Assert.False(v.Contains(user, StringComparison.Ordinal), $"{recipe.Id}: {k}={v}");

            Assert.Contains(plan, l => l == "spack install tree " + Path.Combine(home, "opt").Replace('\\', '/') || l == "spack install tree " + home + "/opt");
        }
    }

    // ── 9. the conda route ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_APalaceInACondaEnvironmentIsFoundAsConda_AndAnUnvalidatedOneIsRefusedByVersion()
    {
        string conda = Path.Combine(_tmp, "miniforge3");
        string bin   = Path.Combine(conda, "envs", "x", "bin");
        Directory.CreateDirectory(bin);
        string banner = File.ReadAllText(Banner("palace-version.txt"));
        string palace = Script(Path.Combine(bin, Windows ? "crf-fake-palace.cmd" : "crf-fake-palace"), banner);

        var d = SolverDiscovery.Create(SolverTool.Palace);
        Isolate(d, "crf-fake-palace");
        d.CondaBases = [conda];

        var found = d.Find(out var rejected);
        Assert.True(found is not null, string.Join("; ", rejected));
        Assert.Equal(SolverHowFound.Conda, found!.HowFound);
        Assert.Equal(palace, found.Path);
        Assert.True(found.Validated);
        Assert.Contains("conda environment", d.DescribeForSettings(found, []));

        Script(palace, "Palace version: deadbee\nSchema version: 1-7-0");
        var readiness = d.Check(new HashSet<SolverCapability> { SolverCapability.DrivenLumpedPorts });
        Assert.Equal(SolverHowFound.Conda, readiness.Installation!.HowFound);
        Assert.False(readiness.Proceeds);
        Assert.Contains("has not validated", readiness.Refusal);
        Assert.Equal(0, d.CapabilityProbesRun);
    }

    // ── 10. consent headless ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_SolverInstallWithoutYesPrintsTheConsentExitsOneAndCreatesNothing()
    {
        string state = Path.Combine(_tmp, "state");
        string tool  = SolverRecipes.For(SolverTool.Palace) is not null ? "palace" : "gmsh";
        var run = RunCli(state, "solver", "install", tool);

        Assert.Equal(1, run.ExitCode);
        if (SolverRecipes.For(SolverHomes.ToolFromId(tool)!.Value) is { } recipe)
        {
            Assert.Contains($"Install {SolverDiscovery.For(recipe.Tool).Name} {recipe.Version}", run.StdErr);
            foreach (var s in recipe.Sources) Assert.Contains(s.Url, run.StdErr);
            Assert.Contains("nothing was downloaded", run.StdErr);
            Assert.Contains("--yes", run.StdErr);
            if (recipe.Tool == SolverTool.Palace) Assert.Contains("ParMETIS", run.StdErr);
        }
        Assert.Equal("", run.StdOut);
        Assert.False(Directory.Exists(state), "declining consent created the state directory");
    }

    // ── 11. the verb holds no install logic ─────────────────────────────────────────────────────

    [Fact]
    public void Gate11_TheSolverVerbAndTheGuiRunnerCallTheInstallFunctions_AndHoldNoInstallLogic()
    {
        foreach (string file in new[] { "src/Cli/Solver.cs", "src/Ui/Layout/Em/SolverInstallRunner.cs" })
        {
            string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), file)));
            Assert.Contains(".Consent(", code);
            Assert.Contains(".Install(", code);
            foreach (string forbidden in new[]
                     { "ProcessStartInfo", "Process.Start", "HttpClient", "File.", "Directory.", "TarFile", "ZipFile",
                       "SHA256", "concretize", "bin/spack", "https://", "install.json", "ArgumentList", ".partial" })
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{file} contains '{forbidden}'");
        }
        string cli = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src/Cli/Solver.cs")));
        Assert.Contains("SolverStatus.Of(", cli);
        string settings = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/Dialogs/Em3dSolverSettingsView.axaml.cs")));
        Assert.Contains("SolverStatus.Of(", settings);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private SolverInstaller Installer(SolverDiscovery discovery)
        => new(Root) { Discovery = _ => discovery, InheritedEnvironment = () => new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" } };

    /// <summary>A Gmsh discovery that sees only this test's install root — no PATH, no default
    /// directories, no Spack, no conda, no preference.</summary>
    private SolverDiscovery IsolatedDiscovery()
    {
        var d = SolverDiscovery.Create(SolverTool.Gmsh);
        Isolate(d, Stub);
        return d;
    }

    private void Isolate(SolverDiscovery d, string command)
    {
        d.PreferredCommand  = () => null;
        d.ReadEnvironment   = name => name == "PATHEXT" ? ".cmd" : null;
        d.CandidateCommands = [command];
        d.SearchDirectories = [];
        d.SpackRoots        = [];
        d.CondaBases        = [];
        d.InstallRoots      = [Root];
        d.CacheDirectory    = Path.Combine(_tmp, "cache");
    }

    /// <summary>A .tgz holding <c>gmsh-fake/bin/crf-fake-gmsh</c>, a stub that prints
    /// <paramref name="version"/> as Gmsh does, optionally padded so a download takes several chunks.</summary>
    private string FakeArchive(string version, int padBytes = 0)
    {
        string src = Path.Combine(_tmp, "upstream-src-" + Guid.NewGuid().ToString("N")[..6]);
        string bin = Path.Combine(src, "gmsh-fake", "bin");
        Directory.CreateDirectory(bin);
        Script(Path.Combine(bin, StubFile), version);
        if (padBytes > 0)
        {
            var noise = new byte[padBytes];
            new Random(24).NextBytes(noise);
            File.WriteAllBytes(Path.Combine(src, "gmsh-fake", "padding.bin"), noise);
        }
        string archive = Path.Combine(_tmp, "upstream-" + Guid.NewGuid().ToString("N")[..6] + ".tgz");
        using (var file = File.Create(archive))
        using (var gz = new GZipStream(file, CompressionLevel.Fastest))
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        return archive;
    }

    private static string Sha(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    private static SolverRecipe Recipe(string archive, string sha, string? extraStep = null)
    {
        var here = SolverRecipes.Current()!.Value;
        string platform = here.Platform switch { RecipePlatform.MacOS => "macos", RecipePlatform.Linux => "linux", _ => "windows" };
        string arch = here.Architecture == RecipeArchitecture.Arm64 ? "arm64" : "x64";
        string json = $$"""
            {
              "id": "gmsh-4.15.2-{{platform}}-{{arch}}", "tool": "gmsh", "version": "4.15.2",
              "platform": "{{platform}}", "architecture": "{{arch}}",
              "layout": "relocatable", "layoutNote": "a test's stub archive",
              "sources": [ { "id": "archive", "url": "{{new Uri(archive).AbsoluteUri}}", "fetchedBy": "circuitRF",
                             "file": "gmsh-fake.tgz", "sha256": "{{sha}}" } ],
              "prerequisites": [],
              "steps": [
                { "name": "Download the stub", "kind": "download", "source": "archive" },
                { "name": "Unpack the stub", "kind": "extract", "source": "archive", "into": "${home}" }
                {{(extraStep is null ? "" : "," + extraStep)}}
              ],
              "program": "${home}/gmsh-fake/bin/{{StubFile}}",
              "measured": { "minutes": null, "diskGB": null, "source": "a test" },
              "knownFailures": []
            }
            """;
        var recipe = SolverRecipes.Parse(json, out string? error);
        Assert.True(recipe is not null, error);
        return recipe!;
    }

    private static string Script(string path, string text)
    {
        string body = Windows
            ? "@echo off\r\n" + string.Join("\r\n", text.Replace("\r", "").Split('\n').Where(l => l.Length > 0).Select(l => "echo " + l)) + "\r\n"
            : "#!/bin/sh\n" + string.Join("\n", text.Replace("\r", "").Split('\n').Where(l => l.Length > 0).Select(l => $"echo '{l}'")) + "\n";
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static DraftSevenSchema Schema()
        => new(JsonDocument.Parse(SolverRecipes.ShippedText["recipe.schema.json"]).RootElement);

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", "");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Banner(string fixture) => Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "banners", fixture);

    private static (int ExitCode, string StdOut, string StdErr) RunCli(string stateDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.Environment[UserStateDirectory.EnvironmentVariable] = stateDir;
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SolverInstallTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private sealed class Inline(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
