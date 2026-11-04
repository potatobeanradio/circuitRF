using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-2 §7 — installing and finding EMerge. Nothing here installs anything or needs EMerge: the recipes are
//  parsed, the probe's answer is canned, and discovery is driven against fake interpreters that print one, through
//  its own SolverDiscovery (SolverDiscovery.Create) with its own environment, roots and cache. Every shipped recipe,
//  EMerge's included, is also validated against the schema by SolverInstallTests' gate 2.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class EmergeInstallTests : IDisposable
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-" + Guid.NewGuid().ToString("N")[..10]);

    public EmergeInstallTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private const string Answer =
        "{\"probe\": 1, \"python\": \"3.13.16\", \"platform\": \"darwin\", \"machine\": \"arm64\", \"cores\": 10, " +
        "\"emerge\": \"3.0.0b2\", \"gmsh\": \"4.15.2\", \"sparseSolvers\": [\"umfpack\"], \"directSolver\": \"SolverSuperLU\"}";

    // ── 1. the recipes ──────────────────────────────────────────────────────────────────────────

    /// <summary>R-emi2-1 — one recipe per platform, every source circuitRF fetches pinned by checksum, and a lock line per
    /// package: an exact version and at least one SHA-256, nothing else.</summary>
    [Fact]
    public void EveryPlatformHasARecipe_PinnedByChecksum_WithAHashOnEveryLockLine()
    {
        var recipes = SolverRecipes.All.Where(r => r.Tool == SolverTool.Emerge).ToList();
        Assert.Equal(["linux-arm64", "linux-x64", "macos-arm64", "macos-x64", "windows-arm64", "windows-x64"],
                     recipes.Select(r => $"{SolverRecipes.Describe(r.Platform).ToLowerInvariant()}-{SolverRecipes.Describe(r.Architecture)}").Order());
        // One marker exists: EMerge's macOS sparse solver, whose only wheel needs macOS 15 (Darwin 24) where circuitRF runs from 13.
        var line = new Regex(@"^[a-z0-9][a-z0-9._-]*==[0-9][0-9A-Za-z.+!-]*( ; platform_release >= ""24"")?( --hash=sha256:[0-9a-f]{64})+$");
        foreach (var r in recipes)
        {
            Assert.All(r.Sources.Where(s => s.FetchedBy == "circuitRF"), s => Assert.Matches("^[0-9a-f]{64}$", s.Sha256 ?? ""));
            var lockStep = Assert.Single(r.Steps, s => s.Kind == RecipeStepKind.Write && s.Path!.EndsWith("requirements.lock", StringComparison.Ordinal));
            var lines = lockStep.Lines.Where(l => !l.StartsWith('#')).ToList();
            Assert.All(lines, l => Assert.Matches(line, l));
            Assert.Contains(lines, l => l.StartsWith($"emerge=={r.Version} ", StringComparison.Ordinal));
            Assert.Equal(r.Platform == RecipePlatform.MacOS, lines.Any(l => l.Contains(" ; platform_release", StringComparison.Ordinal)));
            Assert.NotEmpty(r.ConsentNotes);
            Assert.Contains(r.Steps, s => s.Arguments.Contains("--require-hashes"));
        }
    }

    /// <summary>The update is offered wherever a recipe exists, not only on the machine running the test, and only to circuitRF's own
    /// install of an older tested version.</summary>
    [Fact]
    public void AnOlderOwnInstall_IsOfferedTheUpdate_OnEveryPlatform_AndNothingElseIs()
    {
        var discovery = SolverDiscovery.Emerge;
        string newest = discovery.ValidatedVersions[^1].Release, older = discovery.ValidatedVersions[^2].Release;
        SolverInstallation Own(string v, SolverHowFound how = SolverHowFound.Installed)
            => new(SolverTool.Emerge, "/x/bin/python", v, null, "EMerge " + v, how, "found", v);

        foreach (var platform in Enum.GetValues<RecipePlatform>())
            foreach (var architecture in Enum.GetValues<RecipeArchitecture>())
            {
                var recipe = SolverRecipes.For(SolverTool.Emerge, null, platform, architecture, SolverRecipes.All);
                Assert.Equal(newest, recipe?.Version);
                Assert.True(SolverInstaller.OffersUpdate(discovery, Own(older), recipe), $"{platform} {architecture}");
                Assert.False(SolverInstaller.OffersUpdate(discovery, Own(newest), recipe));
                Assert.False(SolverInstaller.OffersUpdate(discovery, Own(older, SolverHowFound.Settings), recipe));
            }
        var downgrade = SolverRecipes.For(SolverTool.Emerge, null, RecipePlatform.Linux, RecipeArchitecture.X64, SolverRecipes.All)! with { Version = older };
        Assert.False(SolverInstaller.OffersUpdate(discovery, Own(newest), downgrade));
        Assert.False(SolverInstaller.OffersUpdate(discovery, Own(older), null));
    }

    // ── 2. the probe ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheProbesAnswerParses_AmongEmergesLogLines_AndAnythingElseIsRefusedQuotingIt()
    {
        var probe = EmergeProbe.Read($"2026-10-21 20:38:10 | INFO | EMerge v3.0.0b2\n{Answer}\n", out string? why);
        Assert.NotNull(probe);
        Assert.Null(why);
        Assert.Equal(("3.0.0b2", 10, EmergeSpeedTier.Baseline), (probe!.Emerge, probe.Cores, probe.Tier));
        Assert.Equal(["umfpack"], probe.SparseSolvers);
        Assert.Equal(EmergeSpeedTier.Fast, EmergeProbe.Read(Answer.Replace("SolverSuperLU", "SolverOther"), out _)!.Tier);

        Assert.Null(EmergeProbe.Read("Python 3.9.6\nsomething else", out why));
        Assert.Contains("'Python 3.9.6'", why);
        Assert.Null(EmergeProbe.Read("{\"probe\": 1, \"error\": \"import emerge failed: ModuleNotFoundError\"}", out why));
        Assert.Contains("ModuleNotFoundError", why);
    }

    // ── 3. discovery order ──────────────────────────────────────────────────────────────────────

    /// <summary>R-emi2-3a — Settings, then CIRCUITRF_EMERGE, then the copy circuitRF installed; a python on PATH alone is
    /// never found. A named folder stands for its environment's interpreter, and the answer is cached.</summary>
    [Fact]
    public void SettingsBeatsTheEnvironmentBeatsTheHome_AndAPythonOnPathAloneIsNotFound()
    {
        string settings = FakePython(Path.Combine("settings", "venv"));
        string env      = FakePython("env");
        string home     = Path.Combine(_root, "roots", "emerge", "3.0.0b2");
        string homed    = FakePython(Path.Combine("roots", "emerge", "3.0.0b2", "env"));
        new InstallRecord { Tool = SolverTool.Emerge, Version = "3.0.0b2", Home = home, Program = homed, InstalledAt = DateTimeOffset.Now }
            .Write(Path.Combine(home, SolverHomes.RecordFile));
        FakePython("path");

        // A venv folder resolves to its bin/python; the Windows fake is a .cmd, which only a file path names.
        var d = Discovery(Windows ? settings : Path.Combine(_root, "settings", "venv"), env);
        var found = d.Find(out _);
        Assert.Equal((SolverHowFound.Settings, settings), (found!.HowFound, found.Path));
        Assert.Equal(EmergeSpeedTier.Baseline, found.Emerge!.Tier);
        long probes = d.VersionProbesRun;
        Assert.NotNull(d.Find(out _));
        Assert.Equal(probes, d.VersionProbesRun);   // the second answer came from the cache

        Assert.Equal((SolverHowFound.Environment, env), Where(Discovery(null, env)));
        Assert.Equal((SolverHowFound.Installed, homed), Where(Discovery(null, null)));

        var pathOnly = Discovery(null, null, roots: false);
        Assert.Null(pathOnly.Find(out _));
    }

    // ── 4. the tested versions ──────────────────────────────────────────────────────────────────

    /// <summary>Any EMerge a user installed runs; one circuitRF has not tested carries the warning naming the tested
    /// versions, in the run's readiness and on the Settings row alike (Gmsh's rule, brief-em3d-135).</summary>
    [Fact]
    public void AnUntestedVersionRuns_WithAWarningNamingTheTestedOnes()
    {
        string python = FakePython("other", Answer.Replace("\"3.0.0b2\"", "\"3.0.1\""));
        var d = Discovery(python, null);
        var readiness = d.Check(new HashSet<SolverCapability>());
        Assert.True(readiness.Proceeds, readiness.Refusal);
        Assert.False(readiness.Installation!.Validated);
        Assert.Contains("3.0.1", readiness.Warning);
        Assert.Contains("tested: 3.0.0b2", readiness.Warning);
        Assert.EndsWith(readiness.Warning!, d.DescribeForSettings(readiness.Installation, []));
    }

    // ── 5. solver list ──────────────────────────────────────────────────────────────────────────

    /// <summary>R-emi2-5 — <c>solver list</c> lists EMerge because the registry does: the verb names no tool.</summary>
    [Fact]
    public void SolverListTakesEmergeFromTheRegistry_WithNoSpecialCaseInTheCli()
    {
        Assert.Contains(SolverTool.Emerge, Em3dSolvers.Tools);
        Assert.Equal(SolverTool.Emerge, SolverHomes.ToolFromId("emerge"));
        string cli = Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "Solver.cs")), @"//[^\n]*", "");
        Assert.Contains("Em3dSolvers.Tools", cli);
        Assert.DoesNotContain("SolverTool.", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("\"emerge\"", cli, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A discovery wired to this test's world: its preference and CIRCUITRF_EMERGE as given, <c>PATH</c> =
    /// <c>_root/path</c>, the install roots <c>_root/roots</c> (or none), and its own cache.</summary>
    private SolverDiscovery Discovery(string? preferred, string? env, bool roots = true)
    {
        var d = SolverDiscovery.Create(SolverTool.Emerge);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [d.EnvironmentVariable] = env, ["PATH"] = Path.Combine(_root, "path"), ["PATHEXT"] = ".cmd",
        };
        d.PreferredCommand  = () => preferred;
        d.ReadEnvironment   = name => environment.GetValueOrDefault(name);
        d.InstallRoots      = roots ? [Path.Combine(_root, "roots")] : [Path.Combine(_root, "no-roots")];
        d.SearchDirectories = [Path.Combine(_root, "path")];
        d.CacheDirectory    = Path.Combine(_root, "cache");
        return d;
    }

    private static (SolverHowFound, string) Where(SolverDiscovery d)
    {
        var found = d.Find(out var rejected);
        Assert.True(found is not null, string.Join("; ", rejected));
        return (found!.HowFound, found.Path);
    }

    /// <summary>A fake interpreter at <c>&lt;folder&gt;/bin/python</c> (Windows: <c>Scripts\python.cmd</c>) — where a
    /// virtual environment keeps its own — that prints <paramref name="answer"/> whatever it is asked.</summary>
    private string FakePython(string folder, string answer = Answer)
    {
        string dir = Path.Combine(_root, folder, Windows ? "Scripts" : "bin");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Windows ? "python.cmd" : "python");
        File.WriteAllText(path, Windows ? $"@echo off\r\necho {answer}\r\n" : $"#!/bin/sh\ncat <<'EOF'\n{answer}\nEOF\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
