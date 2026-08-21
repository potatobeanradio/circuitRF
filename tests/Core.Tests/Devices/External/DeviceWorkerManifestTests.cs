using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The one fact circuitRF cannot derive — which program evaluates a kit's devices — and how it is
/// read from the file that travels with the kit.
/// </summary>
public sealed class DeviceWorkerManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-manifest-" + Guid.NewGuid().ToString("N")[..8]);

    public DeviceWorkerManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private DeviceWorkerManifest? Write(string json, out string? problem)
    {
        string path = Path.Combine(_dir, DeviceWorkerManifest.FileName);
        File.WriteAllText(path, json);
        return DeviceWorkerManifest.TryRead(path, out problem);
    }

    private DeviceWorkerManifest Read(string json)
    {
        var m = Write(json, out string? problem);
        Assert.Null(problem);
        return Assert.IsType<DeviceWorkerManifest>(m);
    }

    // ── reading ───────────────────────────────────────────────────────────────

    [Fact]
    public void AManifestListsEveryWayOfStartingAWorker()
    {
        var m = Read("""
        { "provider": "AcmeKit",
          "workers": [
            { "platform": "linux-x64", "command": "w", "arguments": ["a", "b"] },
            { "platform": "win-x64",   "command": "w.exe" }
          ] }
        """);

        Assert.Equal("AcmeKit", m.ProviderName);
        Assert.Equal(2, m.Launches.Count);
        Assert.Equal(["a", "b"], m.Launches[0].Arguments);
        Assert.Empty(m.Launches[1].Arguments);
    }

    [Fact]
    public void AManifestWithOneWorker_NeedNotWrapItInAList()
    {
        // The common case should be the short case.
        var m = Read("""{ "provider": "AcmeKit", "command": "worker", "arguments": ["lib"] }""");

        var only = Assert.Single(m.Launches);
        Assert.Equal("worker", only.Command);
        Assert.Equal(["lib"], only.Arguments);
    }

    [Fact]
    public void AManifestNamingNoProvider_TakesTheNameOfItsFolder()
    {
        var m = Read("""{ "command": "worker" }""");

        Assert.Equal(new DirectoryInfo(_dir).Name, m.ProviderName);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        // A manifest is a file people hand-edit; refusing a trailing comma helps nobody.
        var m = Read("""
        {
          // which worker to run
          "workers": [ { "command": "worker", "arguments": ["lib",] }, ]
        }
        """);

        Assert.Equal("worker", Assert.Single(m.Launches).Command);
    }

    // ── refusals ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "workers": [] }""")]
    [InlineData("""{ "workers": [ { "arguments": ["lib"] } ] }""")]   // no command
    public void AManifestThatCannotBeUsed_IsReportedRatherThanThrown(string json)
    {
        // A malformed manifest beside one kit must never stop a workspace from opening.
        var m = Write(json, out string? problem);

        Assert.Null(m);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void AMissingManifest_IsReportedTheSameWay()
    {
        var m = DeviceWorkerManifest.TryRead(Path.Combine(_dir, "absent.json"), out string? problem);

        Assert.Null(m);
        Assert.NotNull(problem);
    }

    // ── choosing for this machine ─────────────────────────────────────────────

    [Fact]
    public void AnExactPlatformBeatsAnOperatingSystem_WhichBeatsACatchAll()
    {
        // So a manifest can give one general entry and override it for a specific machine.
        Assert.True(DeviceWorkerManifest.MatchScore("linux-x64", "linux-x64", "linux")
                  > DeviceWorkerManifest.MatchScore("linux",     "linux-x64", "linux"));

        Assert.True(DeviceWorkerManifest.MatchScore("linux", "linux-x64", "linux")
                  > DeviceWorkerManifest.MatchScore("any",   "linux-x64", "linux"));

        Assert.True(DeviceWorkerManifest.MatchScore("any", "linux-x64", "linux") > 0);
    }

    [Fact]
    public void APlatformForAnotherMachine_DoesNotApplyAtAll()
    {
        Assert.Equal(0, DeviceWorkerManifest.MatchScore("win-x64", "linux-x64", "linux"));
    }

    [Fact]
    public void ABuildForAnInstructionSetTheMachineTranslates_IsUsable()
    {
        // A worker is a separate PROCESS, which is what makes this possible at all: the worker and
        // the model library it loads are the same instruction set as each other, and only circuitRF's
        // own process differs. So a machine whose operating system translates that instruction set
        // can run the kit, and refusing it would refuse a kit that works.
        Assert.True(DeviceWorkerManifest.MatchScore("win-x64", "win-arm64", "win") > 0);
        Assert.True(DeviceWorkerManifest.MatchScore("win-x86", "win-x64",   "win") > 0);
    }

    [Fact]
    public void ATranslatedBuildRanksBelowEveryEntryThatClaimsThisMachine()
    {
        // Every other kind of match is an entry saying it is for this machine; a translated one is an
        // entry for a different machine that this one happens to be able to run. So a kit shipping a
        // native build, or saying its entry works anywhere, is taken at its word first.
        int translated = DeviceWorkerManifest.MatchScore("win-x64", "win-arm64", "win");

        foreach (string claimsThisMachine in new[] { "win-arm64", "win", "any", "" })
            Assert.True(DeviceWorkerManifest.MatchScore(claimsThisMachine, "win-arm64", "win") > translated);
    }

    [Fact]
    public void WhichInstructionSetsAMachineTranslates_IsAFactAboutIt_NotInferredFromTheNames()
    {
        // These strings look exactly like the pair above, and ARM Linux runs no x64 binaries. A rule
        // derived from the spelling would name a build this machine cannot start.
        Assert.Equal(0, DeviceWorkerManifest.MatchScore("linux-x64", "linux-arm64", "linux"));

        // And translation never reaches across operating systems: an executable format is not the
        // instruction set it holds.
        Assert.False(DeviceWorkerManifest.RunsThroughCompatibilityLayer("win-arm64", "linux-x64"));
        Assert.Equal(0, DeviceWorkerManifest.MatchScore("win-x64", "linux-arm64", "linux"));
    }

    [Fact]
    public void AnEntryNamingThisMachineIsNotReportedAsTranslated()
    {
        string here = DeviceWorkerManifest.CurrentRuntimeIdentifier();
        var m = Read($$"""{ "workers": [ { "platform": "{{here}}", "command": "w" } ] }""");

        Assert.NotNull(m.LaunchForThisMachine(out bool translated));
        Assert.False(translated);
    }

    [Fact]
    public void TheMostSpecificEntryIsChosen_HoweverTheyAreOrdered()
    {
        string here = DeviceWorkerManifest.CurrentRuntimeIdentifier();
        var m = Read($$"""
        { "workers": [ { "command": "general" },
                       { "platform": "{{here}}", "command": "specific" },
                       { "command": "also-general" } ] }
        """);

        Assert.Equal("specific", m.LaunchForThisMachine()!.Command);
    }

    [Fact]
    public void AManifestCoveringOnlyOtherMachines_ChoosesNothing()
    {
        var m = Read("""{ "workers": [ { "platform": "somethingelse-x64", "command": "w" } ] }""");

        Assert.Null(m.LaunchForThisMachine());
    }

    // ── resolving paths ───────────────────────────────────────────────────────

    [Fact]
    public void FilesInTheKitBecomeAbsolute_AndEverythingElseIsLeftAlone()
    {
        File.WriteAllText(Path.Combine(_dir, "worker"), "");
        Directory.CreateDirectory(Path.Combine(_dir, "models"));
        File.WriteAllText(Path.Combine(_dir, "models", "lib.bin"), "");

        var m = Read("""
        { "workers": [ { "command": "worker", "arguments": ["models/lib.bin", "--flag", "25"] } ] }
        """);

        var (command, arguments) = m.Resolve(m.Launches[0]);

        Assert.Equal(Path.Combine(_dir, "worker"), command);
        Assert.Equal(Path.Combine(_dir, "models", "lib.bin"), arguments[0]);
        Assert.Equal("--flag", arguments[1]);
        Assert.Equal("25",     arguments[2]);
    }

    [Fact]
    public void AnAbsolutePathIsUsedAsGiven()
    {
        string absolute = Path.Combine(_dir, "elsewhere.bin");
        var m = Read($$"""
        { "workers": [ { "command": "worker", "arguments": [{{System.Text.Json.JsonSerializer.Serialize(absolute)}}] } ] }
        """);

        Assert.Equal(absolute, m.Resolve(m.Launches[0]).Arguments[0]);
    }
}
