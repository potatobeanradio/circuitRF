using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// A kit may name a helper circuitRF itself ships — the Linux VM host that runs Linux-only device
/// models on macOS — by bare name, without knowing where circuitRF was installed. Without this, the
/// name falls through to the system path, finds nothing, and the user is told a program is missing
/// that they never installed and should not have to.
/// </summary>
public sealed class ShippedToolResolutionTests : IDisposable
{
    private readonly string _dir     = Directory.CreateTempSubdirectory("crf-tools").FullName;
    private readonly string _restore = DeviceWorkerManifest.ToolsDirectory;

    public void Dispose()
    {
        DeviceWorkerManifest.ToolsDirectory = _restore;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static DeviceWorkerManifest Read(string dir, string json)
    {
        string path = Path.Combine(dir, DeviceWorkerManifest.FileName);
        File.WriteAllText(path, json);
        var manifest = DeviceWorkerManifest.TryRead(path, out string? problem);
        Assert.True(manifest is not null, problem);
        return manifest!;
    }

    [Fact]
    public void ABareCommand_ResolvesToATheToolCircuitRfShips()
    {
        string kit = Directory.CreateTempSubdirectory("crf-kit").FullName;
        string tool = Path.Combine(_dir, "crf-vmhost");
        File.WriteAllText(tool, "#!/bin/sh\n");
        DeviceWorkerManifest.ToolsDirectory = _dir;

        var manifest = Read(kit, """
            { "provider": "k", "workers": [ { "platform": "any", "command": "crf-vmhost" } ] }
            """);

        var (command, _) = manifest.Resolve(manifest.Launches[0]);
        Assert.Equal(tool, command);
    }

    [Fact]
    public void TheKitsOwnCopyWins_OverTheOneCircuitRfShips()
    {
        // A kit that ships its own build of a tool keeps it; circuitRF's is only a fallback.
        string kit = Directory.CreateTempSubdirectory("crf-kit").FullName;
        File.WriteAllText(Path.Combine(_dir, "crf-vmhost"), "#!/bin/sh\n");
        string kitCopy = Path.Combine(kit, "crf-vmhost");
        File.WriteAllText(kitCopy, "#!/bin/sh\n");
        DeviceWorkerManifest.ToolsDirectory = _dir;

        var manifest = Read(kit, """
            { "provider": "k", "workers": [ { "platform": "any", "command": "crf-vmhost" } ] }
            """);

        Assert.Equal(kitCopy, manifest.Resolve(manifest.Launches[0]).Command);
    }

    [Fact]
    public void ArgumentsNeverResolveIntoCircuitRfsInstall()
    {
        // Arguments name the KIT's files. Letting them resolve inside circuitRF's own install would
        // silently hand a worker a file that has nothing to do with the kit.
        string kit = Directory.CreateTempSubdirectory("crf-kit").FullName;
        File.WriteAllText(Path.Combine(_dir, "crf-vmhost"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(_dir, "models.so"), "not the kit's file");
        DeviceWorkerManifest.ToolsDirectory = _dir;

        var manifest = Read(kit, """
            { "provider": "k",
              "workers": [ { "platform": "any", "command": "crf-vmhost", "arguments": ["models.so"] } ] }
            """);

        Assert.Equal("models.so", manifest.Resolve(manifest.Launches[0]).Arguments.Single());
    }

    [Theory]
    [InlineData("some/crf-vmhost")]
    [InlineData("./crf-vmhost")]
    public void ANameWithAPathSeparator_IsLeftExactlyAsTheKitWroteIt(string command)
    {
        // A separator means the kit meant a path. Rewriting it to circuitRF's own tool would be
        // answering a question the kit did not ask.
        string kit = Directory.CreateTempSubdirectory("crf-kit").FullName;
        File.WriteAllText(Path.Combine(_dir, "crf-vmhost"), "#!/bin/sh\n");
        DeviceWorkerManifest.ToolsDirectory = _dir;

        var manifest = Read(kit, $$"""
            { "provider": "k", "workers": [ { "platform": "any", "command": "{{command}}" } ] }
            """);

        Assert.Equal(command, manifest.Resolve(manifest.Launches[0]).Command);
    }

    [Fact]
    public void AnUnknownBareCommand_IsStillLeftForTheSystemPath()
    {
        string kit = Directory.CreateTempSubdirectory("crf-kit").FullName;
        DeviceWorkerManifest.ToolsDirectory = _dir;

        var manifest = Read(kit, """
            { "provider": "k", "workers": [ { "platform": "any", "command": "python3" } ] }
            """);

        Assert.Equal("python3", manifest.Resolve(manifest.Launches[0]).Command);
    }
}
