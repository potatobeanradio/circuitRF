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
