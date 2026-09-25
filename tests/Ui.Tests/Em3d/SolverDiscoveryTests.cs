using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CircuitRF.Design.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-6 §7 — finding Palace, Gmsh and openEMS. Gates 1-6 and 8; gate 7 is the GPL boundary,
//  tests/Firewall.Tests/SolverBoundaryTests.cs.
//
//  Every test but gate 8 drives its OWN SolverDiscovery (SolverDiscovery.Create) with its own
//  environment, preference and cache directory, and fake executables that print the banners F0
//  recorded (testdata/em3d/f0/banners). Nothing here reads or writes the shared instances, the
//  process environment or the user's state directory, so it cannot disturb — or be disturbed by — any
//  other test in the process.
// ══════════════════════════════════════════════════════════════════════════════════════════════

// Gate 8 reads the SHARED instances, which RunBothTests' gate 8 empties for the length of one run —
// so the two share the 3D-run collection and never overlap. It went unseen while [PalaceFact] skipped
// on every machine with no CIRCUITRF_PALACE; Spack discovery made both run.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class SolverDiscoveryTests : IDisposable
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-solvers-" + Guid.NewGuid().ToString("N")[..10]);

    public SolverDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1. order ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_SettingsBeatsTheEnvironmentBeatsPathBeatsTheDefaultDirectories()
    {
        string settings = Fake("settings", "palace-version.txt");
        string env      = Fake("env", "palace-version.txt");
        Fake("path", "palace-version.txt");
        Fake("default", "palace-version.txt");

        var d = Discovery(SolverTool.Palace, preferred: settings, env: env);
        Assert.Equal((SolverHowFound.Settings, settings), Where(d));

        d = Discovery(SolverTool.Palace, preferred: null, env: env);
        Assert.Equal((SolverHowFound.Environment, env), Where(d));

        d = Discovery(SolverTool.Palace, preferred: null, env: null);
        Assert.Equal((SolverHowFound.Path, Path.Combine(_root, "path", Command)), Where(d));

        d = Discovery(SolverTool.Palace, preferred: null, env: null, path: false);
        Assert.Equal((SolverHowFound.DefaultDirectory, Path.Combine(_root, "default", Command)), Where(d));

        // Emptying the candidate list disables every unprompted route, as its documentation says.
        d = Discovery(SolverTool.Palace, preferred: null, env: null);
        d.CandidateCommands = [];
        Assert.Null(d.Find(out _));
    }

    // ── 2. a broken named program is reported, never replaced ───────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gate2_ABrokenNamedProgramIsReported_AndTheWorkingOneOnPathIsNotUsed(bool namedInSettings)
    {
        Fake("path", "palace-version.txt");                       // a perfectly good Palace on PATH
        string broken = FakePrinting("broken", "Segmentation fault: 11");

        var d = namedInSettings
            ? Discovery(SolverTool.Palace, preferred: broken, env: null)
            : Discovery(SolverTool.Palace, preferred: null, env: broken);

        Assert.Null(d.Find(out var rejected));
        string only = Assert.Single(rejected);
        Assert.Contains(broken, only);
        Assert.Contains("Segmentation fault: 11", only);
        Assert.Contains(namedInSettings ? "set in Settings" : "CIRCUITRF_PALACE", only);
        Assert.Equal(1, d.VersionProbesRun);                      // PATH's Palace was never even asked

        // …and a named path with nothing at it says that rather than starting anything.
        d = Discovery(SolverTool.Palace, preferred: Path.Combine(_root, "nowhere", "palace"), env: null);
        Assert.Null(d.Find(out rejected));
        Assert.Contains("there is no file there", Assert.Single(rejected));
        Assert.Equal(0, d.VersionProbesRun);
    }

    // ── 3. an unvalidated version is a refusal naming the validated list ────────────────────────

    [Fact]
    public void Gate3_AnUnvalidatedVersionIsFound_ButEveryRunIsRefusedNamingTheValidatedList()
    {
        string newer = FakePrinting("newer", "Palace version: 9abcdef\nSchema version: 1-8-0");
        var d = Discovery(SolverTool.Palace, preferred: newer, env: null);

        var found = d.Find(out _);
        Assert.NotNull(found);
        Assert.False(found!.Validated);
        Assert.Equal("9abcdef", found.Version);

        var readiness = d.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Palace));
        Assert.False(readiness.Proceeds);
        Assert.Contains("not validated", readiness.Refusal);
        Assert.Contains("0.18.1 (0dc74cd)", readiness.Refusal);
        Assert.Contains(newer, readiness.Refusal);
        Assert.Empty(readiness.Capabilities);                     // an unvalidated build is not probed
        Assert.Equal(0, d.CapabilityProbesRun);

        // The Settings row says the same, and says the run will refuse it.
        Assert.Contains("NOT validated", d.DescribeForSettings(found, []));
    }

    // ── 4. banner parsing, against the banners F0 recorded ──────────────────────────────────────

    [Theory]
    [InlineData(SolverTool.Palace,  "palace-version.txt",  "0dc74cd", "1-7-0",   "0.18.1")]
    [InlineData(SolverTool.Gmsh,    "gmsh-version.txt",    "4.15.2",  null,      "4.15.2")]
    [InlineData(SolverTool.OpenEms, "openems-banner.txt",  "67d3784", "dcdb62b", "0.37.0-rc3")]
    public void Gate4_EachF0BannerParsesToItsValidatedVersion(SolverTool tool, string fixture, string version, string? companion, string release)
    {
        var parsed = SolverDiscovery.ParseVersion(tool, File.ReadAllText(Banner(fixture)));
        Assert.NotNull(parsed);
        Assert.Equal((version, companion), (parsed!.Version, parsed.Companion));

        var d = Discovery(tool, preferred: Fake("real", fixture), env: null);
        Assert.Equal(release, d.Find(out _)!.Release);
    }

    [Theory]
    [InlineData(SolverTool.Palace,  "git version 2.50.1 (Apple Git-155)")]
    [InlineData(SolverTool.Gmsh,    "Usage: gmsh [options] file")]
    [InlineData(SolverTool.OpenEms, "Palace version: 0dc74cd")]
    public void Gate4_SomethingElseAtThePathIsNotMistakenForTheTool(SolverTool tool, string banner)
        => Assert.Null(SolverDiscovery.ParseVersion(tool, banner));

    // ── 5. the capability probe runs once per binary ────────────────────────────────────────────

    [Fact]
    public void Gate5_ASecondCheckOnTheSameBinaryRunsNoProbe_AndTouchingTheBinaryReprobes()
    {
        string palace = Fake("cap", "palace-version.txt");
        var d = Discovery(SolverTool.Palace, preferred: palace, env: null);
        var needs = SolverDiscovery.CapabilitiesFor(SolverTool.Palace);

        var first = d.Check(needs);
        Assert.True(first.Proceeds, first.Refusal);
        Assert.False(Assert.Single(first.Capabilities).FromCache);
        Assert.Equal(1, d.CapabilityProbesRun);

        // The probe ran in a temporary directory, and that directory is gone (R-em3d6-3a).
        string probeDir = File.ReadAllLines(Path.Combine(_root, "cap", "dry-runs.log")).Single().Trim();
        Assert.Contains("crf-palace-probe-", probeDir);
        Assert.False(Directory.Exists(probeDir));

        // A fresh instance — a new process, in effect — still hits the cache on disk.
        var again = Discovery(SolverTool.Palace, preferred: palace, env: null).Check(needs);
        Assert.True(Assert.Single(again.Capabilities).FromCache);
        var d2 = Discovery(SolverTool.Palace, preferred: palace, env: null);
        d2.Check(needs);
        Assert.Equal(0, d2.CapabilityProbesRun);

        File.SetLastWriteTimeUtc(palace, File.GetLastWriteTimeUtc(palace).AddMinutes(1));
        var touched = d2.Check(needs);
        Assert.False(Assert.Single(touched.Capabilities).FromCache);
        Assert.Equal(1, d2.CapabilityProbesRun);
    }

    [Fact]
    public void Gate5_AFailedDryRunIsARefusalNamingTheCapability()
    {
        string palace = Fake("nocap", "palace-version.txt", dryRunOk: false);
        var d = Discovery(SolverTool.Palace, preferred: palace, env: null);
        var readiness = d.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Palace));

        // A "no" is never cached: the same exit also ends an environment problem the user can fix.
        d.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Palace));
        Assert.Equal(2, d.CapabilityProbesRun);

        Assert.False(readiness.Proceeds);
        Assert.Contains("driven solves with lumped ports", readiness.Refusal);
        Assert.Contains("validation failed", readiness.Refusal);   // Palace's own sentence, carried
        Assert.Contains("Rebuild Palace", readiness.Refusal);
    }

    // ── 6. the refusal's order, and Windows ─────────────────────────────────────────────────────

    [Fact]
    public void Gate6_TheMissingToolRefusalNamesTheTool_ThenHowToPointAtIt_ThenTheInstallSection_AndOnWindowsNamesOpenEms()
    {
        var palace = SolverDiscovery.Create(SolverTool.Palace);
        string windows = palace.DescribeFailure(["'/x/palace': it could not be started"], windows: true);
        string mac     = palace.DescribeFailure([], windows: false);

        int tool    = windows.IndexOf("Palace was not found", StringComparison.Ordinal);
        int point   = windows.IndexOf("Settings ▸ 3D EM", StringComparison.Ordinal);
        int env     = windows.IndexOf("CIRCUITRF_PALACE", StringComparison.Ordinal);
        int install = windows.IndexOf(SolverDiscovery.ManualInstallSection, StringComparison.Ordinal);
        Assert.True(tool >= 0 && tool < point && point < env && env < install, windows);

        Assert.Contains("does not run natively on Windows", windows);
        Assert.Contains("Linux subsystem", windows);
        Assert.Contains("openEMS runs natively", windows);
        Assert.DoesNotContain("openEMS", mac);

        // Only Palace carries it: openEMS IS the Windows answer.
        Assert.DoesNotContain("natively", SolverDiscovery.Create(SolverTool.OpenEms).DescribeFailure([], windows: true));
    }

    // ── 7b. a Palace built by Palace's own Spack recipe is found with no setup ─────────────────

    /// <summary>
    /// Palace's recipe installs with no view, into a PADDED tree (<c>padded_length: 256</c>), so the
    /// program is on no PATH and in no default directory. Discovery reads Spack's install database and
    /// finds it after every other route; the MPI launcher is the one that database says this Palace
    /// links — not a newer install's, and not one that is not installed.
    /// </summary>
    [Fact]
    public void Gate7b_ASpackInstalledPalace_AndTheMpiItLinks_AreFoundFromTheInstallDatabase()
    {
        string tree = Path.Combine(_root, "spack", "__spack_path_placeholder__", "__spack_path_placeholder__", "__spack_pat");
        string Prefix(string name) => Path.Combine(tree, "darwin-m3", name);
        string palacePrefix = Prefix("palace-0.18.1-aaaa"), mpiPrefix = Prefix("openmpi-5.0.10-bbbb");
        Directory.CreateDirectory(Path.Combine(palacePrefix, "bin"));
        Directory.CreateDirectory(Path.Combine(mpiPrefix, "bin"));
        Directory.CreateDirectory(Path.Combine(Prefix("openmpi-4.1.0-cccc"), "bin"));
        File.Copy(Fake("scratch", "palace-version.txt"), Path.Combine(palacePrefix, "bin", Command));
        if (!Windows) File.SetUnixFileMode(Path.Combine(palacePrefix, "bin", Command), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(mpiPrefix, "bin", "mpirun"), "");
        File.WriteAllText(Path.Combine(Prefix("openmpi-4.1.0-cccc"), "bin", "mpirun"), "");

        string Esc(string p) => p.Replace("\\", "\\\\");
        Directory.CreateDirectory(Path.Combine(tree, ".spack-db"));
        File.WriteAllText(Path.Combine(tree, ".spack-db", "index.json"), $$"""
            { "database": { "version": "8", "installs": {
              "aaaa": { "installed": true, "path": "{{Esc(palacePrefix)}}", "installation_time": 100,
                        "spec": { "name": "palace", "version": "0.18.1", "dependencies": [
                          { "name": "cmake",   "hash": "dddd", "parameters": { "deptypes": ["build"], "virtuals": [] } },
                          { "name": "openmpi", "hash": "bbbb", "parameters": { "deptypes": ["build", "link"], "virtuals": ["mpi"] } } ] } },
              "bbbb": { "installed": true,  "path": "{{Esc(mpiPrefix)}}", "installation_time": 50, "spec": { "name": "openmpi", "version": "5.0.10" } },
              "cccc": { "installed": true,  "path": "{{Esc(Prefix("openmpi-4.1.0-cccc"))}}", "installation_time": 200, "spec": { "name": "openmpi", "version": "4.1.0" } },
              "eeee": { "installed": false, "path": "{{Esc(Prefix("palace-0.19.0-eeee"))}}", "installation_time": 300, "spec": { "name": "palace", "version": "0.19.0" } }
            } } }
            """);

        var d = Discovery(SolverTool.Palace, preferred: null, env: null, path: false);
        d.SearchDirectories = [Path.Combine(_root, "nothing-here")];
        d.SpackRoots        = [Path.Combine(_root, "spack")];
        Assert.Equal((SolverHowFound.Spack, Path.Combine(palacePrefix, "bin", Command)), Where(d));
        Assert.Contains("Spack", d.DescribeForSettings(d.Find(out _), []));

        Assert.Equal(Path.Combine(mpiPrefix, "bin", "mpirun"),
                     SpackInstalls.MpiLauncherFor(Path.Combine(palacePrefix, "bin", Command), d.SpackRoots));
        Assert.Null(SpackInstalls.MpiLauncherFor(Path.Combine(_root, "elsewhere", "palace"), d.SpackRoots));

        // Earlier routes still win, and a tool with no Spack package never looks.
        Fake("default", "palace-version.txt");
        var withDefault = Discovery(SolverTool.Palace, preferred: null, env: null, path: false);
        withDefault.SpackRoots = d.SpackRoots;
        Assert.Equal(SolverHowFound.DefaultDirectory, Where(withDefault).Item1);
        d.SpackPackage = null;
        Assert.Null(d.Find(out _));
    }

    // ── 8. the real tools, when this machine has F0's installs ──────────────────────────────────

    /// <summary>On a machine with F0's three installs, the SHIPPED discovery finds all three at their
    /// validated versions. Skipped, with a reason, anywhere else (overview §1i).</summary>
    [RealSolversFact]
    public void Gate8_TheRealInstallsAreFoundAndValidated()
    {
        foreach (var tool in new[] { SolverTool.Palace, SolverTool.Gmsh, SolverTool.OpenEms })
        {
            var found = SolverDiscovery.For(tool).Find(out var rejected);
            Assert.True(found is not null, string.Join("; ", rejected));
            Assert.True(found!.Validated, $"{tool}: {found.DescribeVersion()} at {found.Path}");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string Command => Windows ? "crf-fake-solver.cmd" : "crf-fake-solver";

    /// <summary>
    /// A discovery wired to this test's own world: its preference and environment variable as given,
    /// <c>PATH</c> = <c>_root/path</c> (or nothing), the default directory <c>_root/default</c>, and its
    /// own capability cache.
    /// </summary>
    private SolverDiscovery Discovery(SolverTool tool, string? preferred, string? env, bool path = true)
    {
        var d = SolverDiscovery.Create(tool);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [d.EnvironmentVariable] = env,
            ["PATH"]    = path ? Path.Combine(_root, "path") : null,
            ["PATHEXT"] = ".cmd",
        };
        d.PreferredCommand  = () => preferred;
        d.ReadEnvironment   = name => environment.GetValueOrDefault(name);
        d.CandidateCommands = ["crf-fake-solver"];
        d.SearchDirectories = [Path.Combine(_root, "default")];
        d.CacheDirectory    = Path.Combine(_root, "cache");
        return d;
    }

    private static (SolverHowFound, string) Where(SolverDiscovery d)
    {
        var found = d.Find(out var rejected);
        Assert.True(found is not null, string.Join("; ", rejected));
        return (found!.HowFound, found.Path);
    }

    private static string Banner(string fixture)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "testdata", "em3d", "f0", "banners", fixture);
    }

    /// <summary>A fake that prints one of F0's banners for any question, and answers Palace's dry run —
    /// logging the directory it ran in — with success or with Palace's own validation failure.</summary>
    private string Fake(string folder, string bannerFixture, bool dryRunOk = true)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "dry-runs.log");
        string ok  = "Dry-run: No errors detected in configuration file \"probe.json\"";
        string bad = "At [\"Solver\"]: validation failed for additional property 'Bogus'";
        string script = Windows
            ? $"""
               @echo off
               if "%~2"=="--dry-run" (
                 cd >> "{log}"
                 echo {(dryRunOk ? ok : bad)}
                 exit /b {(dryRunOk ? 0 : 134)}
               )
               type "{Banner(bannerFixture)}"
               """
            : $"""
               #!/bin/sh
               for a in "$@"; do
                 if [ "$a" = "--dry-run" ]; then
                   pwd >> "{log}"
                   echo '{(dryRunOk ? ok : bad)}'
                   exit {(dryRunOk ? 0 : 134)}
                 fi
               done
               cat "{Banner(bannerFixture)}"
               """;
        return WriteScript(Path.Combine(dir, Command), script);
    }

    private string FakePrinting(string folder, string text)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        string script = Windows
            ? "@echo off\r\n" + string.Join("\r\n", text.Split('\n').Select(l => "echo " + l)) + "\r\n"
            : "#!/bin/sh\n" + string.Join("\n", text.Split('\n').Select(l => $"echo '{l}'")) + "\n";
        return WriteScript(Path.Combine(dir, Command), script);
    }

    private static string WriteScript(string path, string script)
    {
        File.WriteAllText(path, Windows ? script.Replace("\r\n", "\n").Replace("\n", "\r\n") : script.Replace("\r\n", "\n"));
        if (!Windows)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}

/// <summary>Skips, naming what is missing, unless the shipped discovery finds all three programs.</summary>
public sealed class RealSolversFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
    {
        var missing = new[] { SolverTool.Palace, SolverTool.Gmsh, SolverTool.OpenEms }
            .Where(t => SolverDiscovery.For(t).Find(out _) is null)
            .Select(t => SolverDiscovery.For(t).Name)
            .ToList();
        return missing.Count == 0
            ? null
            : $"not installed here (or not findable without Settings or CIRCUITRF_*): {string.Join(", ", missing)}. "
            + "Gate 8 needs F0's installs — docs/user/src/reference/em-setup.md#install-3d-solvers";
    });

    public RealSolversFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}
