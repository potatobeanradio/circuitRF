using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The hand-off that makes a kit part simulable with nothing configured: importing a kit carries its
/// device-provider manifest into the workspace, where provider resolution will later find it.
///
/// <para>Without this the user imports a kit, places a part, presses Run, and is told the provider
/// is unavailable — with the fix being to hand-copy a file they have no reason to know about.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkProviderHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-handoff-" + Guid.NewGuid().ToString("N")[..8]);

    private string KitDir       => Path.Combine(_root, "kit");
    private string WorkspaceDir => Path.Combine(_root, "ws");
    private string InstalledKit => Path.Combine(WorkspaceDir, PdkPartInstaller.InstallFolderName, "SampleKit");

    public PdkProviderHandoffTests()
    {
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(WorkspaceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteKitManifest(string json)
        => File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName), json);

    private PdkPartInstaller.InstallOutcome Install()
        => PdkPartInstaller.Install(
               new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" }, WorkspaceDir);

    private DeviceWorkerManifest ReadInstalledManifest()
    {
        string path = Path.Combine(InstalledKit, DeviceWorkerManifest.FileName);
        var manifest = DeviceWorkerManifest.TryRead(path, out string? problem);
        Assert.Null(problem);
        return Assert.IsType<DeviceWorkerManifest>(manifest);
    }

    // ── the hand-off ──────────────────────────────────────────────────────────

    [Fact]
    public void AKitThatDescribesHowToSimulateIt_HasThatCarriedIntoTheWorkspace()
    {
        WriteKitManifest("""
        { "workers": [ { "platform": "any", "command": "worker", "arguments": ["models/lib.bin"] } ] }
        """);

        Install();

        Assert.True(File.Exists(Path.Combine(InstalledKit, DeviceWorkerManifest.FileName)));
        Assert.Equal("SampleKit", ReadInstalledManifest().ProviderName);
    }

    [Fact]
    public void TheCopyAnswersToTheKitName_WhateverTheOriginalCalledItself()
    {
        // Each installed cell records Provider = the kit name, so that is what a netlist asks for.
        // A copy keeping some other name leaves every step working and only Run failing.
        WriteKitManifest("""
        { "provider": "something-else", "workers": [ { "platform": "any", "command": "worker" } ] }
        """);

        Install();

        Assert.Equal("SampleKit", ReadInstalledManifest().ProviderName);
    }

    [Fact]
    public void TheCopyStillReachesTheWorkerAndModelFiles_WhichNeverLeftTheKit()
    {
        // This is the whole reason the copy records where the kit was. Relative paths in the
        // original are relative to the KIT, and the copy does not live there.
        Directory.CreateDirectory(Path.Combine(KitDir, "models"));
        File.WriteAllText(Path.Combine(KitDir, "worker"), "");
        File.WriteAllText(Path.Combine(KitDir, "models", "lib.bin"), "");

        WriteKitManifest("""
        { "workers": [ { "platform": "any", "command": "worker", "arguments": ["models/lib.bin"] } ] }
        """);

        Install();

        var manifest = ReadInstalledManifest();
        var (command, arguments) = manifest.Resolve(manifest.Launches[0]);

        Assert.Equal(Path.Combine(KitDir, "worker"), command);
        Assert.Equal(Path.Combine(KitDir, "models", "lib.bin"), arguments[0]);
    }

    [Fact]
    public void FilesPlacedBesideTheCopy_WinOverTheOnesInTheKit()
    {
        // So a user can override a kit's worker for one workspace by dropping a file in, which is
        // the only escape hatch they have that needs no configuration.
        File.WriteAllText(Path.Combine(KitDir, "worker"), "");
        WriteKitManifest("""{ "workers": [ { "platform": "any", "command": "worker" } ] }""");

        Install();
        File.WriteAllText(Path.Combine(InstalledKit, "worker"), "");

        var manifest = ReadInstalledManifest();

        Assert.Equal(Path.Combine(InstalledKit, "worker"), manifest.Resolve(manifest.Launches[0]).Command);
    }

    [Fact]
    public void EveryPlatformTheKitCoversSurvivesTheCopy()
    {
        // A workspace opened on another machine must still find that machine's worker.
        WriteKitManifest("""
        { "workers": [ { "platform": "linux-x64", "command": "w" },
                       { "platform": "win-x64",   "command": "w.exe" },
                       { "platform": "osx-arm64", "command": "w-vm" } ] }
        """);

        Install();

        var platforms = ReadInstalledManifest().Launches.Select(l => l.Platform).ToArray();

        Assert.Equal(["linux-x64", "win-x64", "osx-arm64"], platforms);
    }

    // ── kits that describe nothing ────────────────────────────────────────────

    [Fact]
    public void AKitWithNoSuchDescription_ImportsSilently()
    {
        // The ordinary case. Its parts still place and draw; only simulating them needs a manifest,
        // so saying anything here would be noise on nearly every import.
        var outcome = Install();

        Assert.False(File.Exists(Path.Combine(InstalledKit, DeviceWorkerManifest.FileName)));
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Contains("simulat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AKitWhoseDescriptionIsBroken_SaysSoWithoutFailingTheImport()
    {
        // Everything else about the kit still works, so the import must not be lost over this.
        WriteKitManifest("{ not json at all");

        var outcome = Install();

        Assert.Contains(outcome.Diagnostics, d => d.Contains("simulate", StringComparison.OrdinalIgnoreCase));
    }

    // ── end to end ────────────────────────────────────────────────────────────

    [Fact]
    public void AfterImport_ResolutionFindsTheKitByTheNameItsPartsWereInstalledUnder()
    {
        // The netlist writes Provider=<kit name>. That name has to be the one resolution answers to,
        // or every step works and the last one fails.
        File.WriteAllText(Path.Combine(KitDir, "worker"), "");
        WriteKitManifest("""{ "workers": [ { "platform": "any", "command": "worker" } ] }""");

        Install();

        string kitsRoot = Path.Combine(WorkspaceDir, PdkPartInstaller.InstallFolderName);
        string? started = null;

        var resolver = new DeviceWorkerProviderResolver([kitsRoot],
            (name, command, args) =>
            {
                started = command;
                return new DeviceWorkerProvider(name, new StubTransport());
            });

        var provider = resolver.Resolve("SampleKit");

        Assert.NotNull(provider);
        Assert.Equal("SampleKit", provider!.Name);
        Assert.Equal(Path.Combine(KitDir, "worker"), started);
    }

    /// <summary>A transport that is never spoken to — these tests stop at "which worker would start".</summary>
    private sealed class StubTransport : IDeviceWorkerTransport
    {
        public Stream Requests          => Stream.Null;
        public Stream Replies           => Stream.Null;
        public string Origin            => "stub";
        public bool   IsAlive           => true;
        public string RecentErrorOutput => "";
        public void   Dispose() { }
    }
}
